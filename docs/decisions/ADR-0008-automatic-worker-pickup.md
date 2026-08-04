# ADR-0008: Automatic Single-Worker Pickup

**Status:** Accepted

**Date:** 2026-08-04

---

## Context

ADR-0006 deliberately built the runner bridge around *explicit* invocation: a human selects a
Started session, copies its task ID and session ID, and runs `FindFamiliar.Runner` with both. That
was the right boundary for proving execution, networking, authentication and repository safety
independently of automation, and Sprint 06.5 proved the whole chain end to end against a locally
authenticated Claude Code runtime over a private tailnet.

What remained was pure operational ceremony:

```text
Start Familiar -> start the worker -> create a session -> copy two GUIDs -> run the runner -> wait
```

ADR-0006 explicitly listed "automatic work discovery, polling, or claiming" and "a `Run`/`Lease`
table" as non-goals, with the reasoning that a lease "only earns its cost once multiple runners
might race for the same work." Sprint 07 is where that cost is earned: the moment a worker
discovers work on its own, two workers — or one worker restarted mid-run — *can* race for the same
session, and the one-`Started`-session-per-task invariant no longer settles who owns an execution.

The goal of this ADR is the smallest coordination layer that removes the manual relay without
weakening a single guarantee ADR-0003, ADR-0005 or ADR-0006 established.

---

## Decision

### The server decides; the worker asks

A worker never selects its own work. It reports who it is and what it can service, and the server
grants it at most one claim per request. This keeps ADR-0005's rule intact — session authority
lives on the server — and it is why there is no "list eligible sessions" route: a separate list
call followed by a claim call is exactly the race this design exists to prevent.

### `Worker` entity

One new table (`Workers`), one migration (`WorkerRegistrationAndSessionClaims`):

- `WorkerKey` — stable, administrator-chosen, unique. A worker that restarts is the *same* row, not
  a new one, which is what makes heartbeat history and the enabled flag durable.
- `DisplayName`, `Enabled`, `Capabilities` (canonical comma-separated `AgentSessionRole` names),
  `RegisteredUtc`, `LastHeartbeatUtc`, `LastClaimUtc`.

The row holds **no** machine-specific configuration: no repository path, no drive letter, no
adapter path, no credential. That is a hard boundary, not an omission (see "Repository mapping").

### Registration is heartbeat

There is no separate registration endpoint. The first heartbeat from an unknown `WorkerKey` creates
the row; every later heartbeat updates `LastHeartbeatUtc` and the reported capabilities. One code
path, no ordering requirement, and no way to have a worker that heartbeats but was never
registered.

A heartbeat never re-enables a disabled worker. An administrator's decision to park a worker
outlives the worker's own opinion of itself.

### Availability is derived, never persisted, never authoritative

`WorkerAvailability` is computed at read time from the heartbeat age: `Online` within 90 seconds,
`Stale` within 10 minutes, `Offline` beyond that. It is stored nowhere and consulted by nothing
except the operator page.

This matters more than it looks. If availability were persisted and consulted during claiming, a
clock skew or a missed heartbeat could silently strand or double-issue work. Session `Status` and
the claim lease remain the only things that decide whether work may be executed — exactly as
ADR-0005 requires. Heartbeat answers "should I worry about this worker?", never "may this session
run?".

### Claiming is one conditional UPDATE

`WorkerCoordinationService.ClaimNextAsync` reads a bounded list of candidate sessions, then for each
candidate issues a single `ExecuteUpdateAsync` whose `WHERE` clause re-checks every condition that
another worker could have invalidated in the meantime:

```sql
UPDATE AgentSessions
   SET ClaimedByWorkerId = @worker, ClaimId = @claimId,
       ClaimedUtc = @now, ClaimExpiresUtc = @expiry
 WHERE Id = @candidate
   AND Status = 'Started'
   AND EXISTS (SELECT 1 FROM Workers WHERE Id = @worker AND Enabled = 1
               AND Capabilities = @capabilitiesRead)
   AND (ClaimedByWorkerId IS NULL OR ClaimExpiresUtc IS NULL OR ClaimExpiresUtc <= @now)
```

Exactly one racer sees `rowsAffected == 1`; the losers see `0` and move to the next candidate. The
candidate walk is capped (20) so a heavily contended queue can never turn one request into an
unbounded write loop. `Concurrent_claims_for_one_session_grant_exactly_one_owner` proves this with
eight parallel claims against one session and independent `DbContext` instances, on a real
file-backed SQLite database — an in-memory provider would not prove serialization.

The read-then-conditional-update shape was chosen over a single `UPDATE ... WHERE id IN (SELECT
...)` because it keeps the eligibility query (which joins to `Tasks` for the project filter) out of
the write statement, which SQLite's `UPDATE ... FROM` support makes awkward, while losing none of
the atomicity: correctness rests entirely on the guarded `WHERE`, not on the read.

### Eligibility

A session is offered only when **all** of the following hold:

- `Status == Started`;
- unclaimed, or its lease has expired;
- the worker is registered and enabled;
- the worker's persisted capabilities include the session's role;
- the session's project is one the worker declared a local repository mapping for;
- the worker's local configuration is read-only mode (enforced worker-side at config load).

### Lease, not lock

Each claim carries a bounded lease (`ClaimExpiresUtc`, 30–3600s) and a unique `ClaimId` fencing
token. The worker renews the lease while the adapter is running. Renewal is one conditional update
that requires the same worker, the same `ClaimId`, a still-unexpired lease, a `Started` session and
an enabled worker. If heartbeat or renewal fails, the worker terminates the adapter process tree and
does not submit a result.

When a worker crashes, renewal stops and the session becomes claimable after expiry. A replacement
claim receives a new `ClaimId`. Result capture, durable cancellation and release all require the
current generation, and capture/cancellation also reject an expired lease. A stale worker therefore
cannot complete or cancel a replacement worker's claim. `Status` and `ClaimId` are EF concurrency
tokens, so simultaneous terminal submissions still commit at most one transaction.

This is lease-based recovery, not a promise that an external provider can never overlap across every
possible network partition. It does guarantee that only the live claim generation can change
Familiar's workflow state and that the maintained worker stops local provider work when ownership
cannot be confirmed.

### Repository mapping stays machine-local

The shared database must never contain a Windows drive letter or a local repository path. So the
mapping is inverted: the **worker** sends the set of project IDs it can service (project GUIDs are
already the server's own data), and the server filters candidates by that set. Nothing
machine-specific travels to the server, and nothing machine-specific is persisted.

The worker's local `worker.json` (path from `FAMILIAR_WORKER_CONFIG`, gitignored, never committed)
maps each project GUID to a worktree, an allowed root and a mode. At execution time the worker sets
those as the adapter's existing `FAMILIAR_CLAUDE_*` environment variables for that one child
process. The compiled adapter's containment checks (ADR-0007) are unchanged and still authoritative
— the worker chooses *which* repository, the adapter still decides whether that repository is
allowed.

The Familiar bearer token is deliberately *not* a field in `worker.json`. It comes only from
`FAMILIAR_RUNNER_TOKEN`, so the configuration file never holds a secret and can be diffed, backed
up or pasted into a support request without leaking one.

### Execution reuses the existing chain verbatim

`RunnerEngine.RunAsync` was split, not duplicated. The adapter invocation, failure classification,
durable cancellation and single result submission now live in `ExecuteAssignmentAsync`, which takes
a `RunnerExecutionRequest`. Both entry points build one:

```text
explicit CLI  -> fetch assignment -> RunnerExecutionRequest -+
                                                             +-> ExecuteAssignmentAsync -> adapter
worker loop   -> claim (assignment included) ----------------+
```

There is still exactly one implementation of "run the adapter and resolve the session," so every
ADR-0006 failure guarantee — timeout kills the process tree, malformed output cancels durably, an
ambiguous post-submission transport failure never auto-cancels — applies identically to
automatically claimed work. No shell was introduced, no protocol version changed: the claim
response is additive, and `ContractVersion` stays `1`.

The claim response carries its assignment inline. Before returning it, the server re-checks that the
projected session is still `Started` and still matches the claimed role and context revision. If it
does not, the server releases only that exact claim generation.

### The worker loop

`FindFamiliar.Runner worker` loads local configuration, heartbeats on its own interval, requests one
claim, executes it, and repeats. Heartbeats continue during idle backoff and adapter execution, so a
healthy worker does not become `Stale` merely because polling backed off or provider work is slow.
The worker refuses to claim after a failed heartbeat and validates the heartbeat and claim worker
identity before execution. While executing, it renews the lease at one third of the remaining lease
window.

Idle polls back off exponentially from `pollSeconds` to `maxPollSeconds`; a successful execution
resets the interval and re-polls immediately. Every non-executing path goes through the same delay,
so there is no route around the loop that can busy-spin. Ctrl+C, service shutdown, heartbeat loss
and claim-renewal loss all terminate the adapter process tree and wait for it to exit before the
worker reports that execution has stopped.

The loop makes no workflow decisions: it never starts a session, never picks a role, never chains
Planner to Implementer, and never completes a task. Those remain human actions in the `/Work` queue,
unchanged.

### Operator visibility

A read-only `/Workers` page lists each registered worker with availability, enabled state,
capabilities, last heartbeat, its active claim (linked to the task) and lease timing. It performs
no writes — asserted by test, because a page that "helpfully" refreshed a heartbeat would make the
UI a source of workflow truth.

### Windows teardown stabilization

Sprint 06.5 saw runs fail on Windows *after* every assertion passed, during temp-directory cleanup,
because a pooled SQLite connection had not released its file handle yet.

`TemporaryDirectoryCleanup` fixes the cause before working around the symptom: it calls
`SqliteConnection.ClearAllPools()` to release pooled handles deterministically, and
`TemporarySqliteDatabase` now tracks and disposes every context it hands out. A bounded retry (10
attempts, 50 ms, with a finalizer pass) absorbs the short OS-level handle-release lag that can
remain. On Windows it also clears deletion-blocking file attributes in Git worktree fixtures before
each attempt. The final failure is **rethrown** — a directory still locked after the full budget is a real
leak, and this helper must never convert one into a silent pass. No assertion was weakened and no
unbounded sleep was introduced.

---

## Consequences

### Positive

- Starting a Planner session is now sufficient: no GUIDs are copied and no runner command is typed.
- Duplicate accepted results are prevented by atomic claim generation, fencing, concurrency tokens
  and the pre-existing terminal-status replay rejection.
- Worker failure is self-healing within one lease period, with no operator action and no new
  reconciliation code.
- The database gained one table and four nullable columns. No existing subsystem was replaced, no
  contract version changed, and the explicit CLI invocation still works exactly as before.
- Repository paths and credentials remain strictly on the worker host, so the shared database stays
  portable and safe to back up.

### Negative

- There is now persistent coordination state (`Workers`, the claim columns) that ADR-0006
  explicitly deferred. This is the cost of automation and was accepted knowingly.
- A sufficiently long network partition can still allow external provider work to overlap after a
  lease expires, because no lease can provide mathematical exactly-once execution across a lost
  network. The stale generation is fenced from Familiar state, and the maintained worker terminates
  its local process as soon as heartbeat or renewal fails.
- Capabilities are trusted as reported. A worker that claims a role it cannot actually run will
  claim and then fail that work, which is durably cancelled and retryable — but it is not prevented
  server-side.
- The one-`Started`-session-per-task invariant remains application-level (unchanged from ADR-0005).
  The claim is now genuinely concurrency-safe at the database level, but session *creation* is not.

---

## Alternatives Considered

### Separate "list eligible work" and "claim it" endpoints

Rejected — this is the race. Two workers list the same session, both claim, and correctness then
depends on whatever the claim does about it. Collapsing discovery and ownership into one atomic
operation makes the race structurally impossible rather than handled.

### Server-push (SignalR/WebSocket) instead of polling

Rejected as disproportionate. A conservative poll with backoff has no connection state to manage, no
reconnect semantics, no dependency, and survives a worker or server restart with no coordination.
Push would buy latency this sprint has no requirement for.

### Storing repository paths on the server, keyed by project

Rejected outright. It would put Windows drive letters and machine-local paths into a shared,
backed-up database, break the moment a second worker had a different layout, and hand assignment
content a path surface it currently cannot reach. Inverting the mapping (worker sends project IDs)
costs nothing and keeps the boundary absolute.

### Claim status as a new `AgentSessionStatus` value (e.g. `Claimed`)

Rejected. `Status` is the workflow authority read by the work queue, the capture service, the
cancellation service and the assignment endpoint. Adding an execution-transport concern to it would
mean every one of those had to learn that `Claimed` is "still Started, really." Separate nullable
claim columns leave all existing status logic untouched.

### A distributed scheduler / job queue library

Rejected as far beyond scope. Sprint 07 needs one worker to pick up one session. Three nullable
columns and one conditional UPDATE do that, and can be deleted or replaced later without a
migration path through someone else's abstractions.

### A lease longer than every possible adapter run, without renewal

Rejected. The server starts the lease before the adapter, the configured timeout can equal the
maximum lease, and process scheduling or network delay consumes part of the window. Periodic guarded
renewal is small, directly testable and makes healthy long-running execution explicit.

---

## Non-Goals

- Conversation, chat UI, or natural-language intent.
- Automatic task creation, or Planner -> Implementer -> Reviewer chaining.
- Edit-worktree execution, commits, pushes, or deployment.
- Provider routing, multi-provider selection, or advanced scheduling.
- Multi-user authorization, or any claim that the app may be exposed publicly.
- Notifications.
- Work stealing, priority, or fair-share scheduling across many workers.
