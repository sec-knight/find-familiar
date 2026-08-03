# ADR-0005: Work Queue and Session Lifecycle

**Status:** Accepted

**Date:** 2026-08-02

---

## Context

ADR-0004 gave a Started session a generated assignment packet, but two lifecycle gaps surfaced during Sprint 04
dogfooding:

1. **Revision-order bug.** `OnPostStartSessionAsync` read `task.Project.ContextRevision` into the new session's
   `ContextRevisionRead` *before* calling `IncrementContextRevision()`. The session therefore always started one
   revision behind the project it just entered canonical context for, so its own freshly generated packet
   immediately showed a `STALE CONTEXT WARNING` that did not describe any real staleness.
2. **No cancellation, no queue.** Nothing stopped a task from accumulating more than one `Started` session at a
   time, and nothing let a human explain why a session was abandoned instead of completed. There was also no single
   place that told a user what to do next for a given task — that required reading the session list and inferring
   the next role by hand.

Both gaps are answerable entirely from state Find Familiar already persists (`AgentSession.Status`,
`AgentSession.Role`, `AgentSession.StartedUtc`/`CompletedUtc`, `FamiliarTask.Status`). No new table, column, or
migration is needed.

---

## Decision

### 1. Fix `StartSession` ordering and enforce at most one `Started` session per task

`OnPostStartSessionAsync` now, in order:

1. validates `NewSession` in isolation (unchanged);
2. loads the task with its project;
3. queries whether any `AgentSession` for this task already has `Status == Started`; if so, adds a scoped
   `NewSession.Role` validation error, reloads the page, and returns without writing anything;
4. captures one `startedUtc` timestamp;
5. calls `task.Project.IncrementContextRevision()` **before** constructing the new `AgentSession`, then reads the
   now-incremented `task.Project.ContextRevision` into `ContextRevisionRead`;
6. adds the session and calls `SaveChangesAsync` once.

This is enforced through the normal application command path only — an authoritative database read at the moment of
the command, not a unique index or transaction isolation level. A legacy or corrupt row that already violates the
invariant (for example, seeded directly into the database) is never silently repaired; the work queue (below)
surfaces it instead.

The existing task-timestamp/status behavior of `StartSession` (it never touched `FamiliarTask.UpdatedUtc` or
`Status`) is preserved unchanged — only the revision ordering and the new rejection path were added.

### 2. Atomic session cancellation

A new bound `SessionCancellationInput` (`SessionId: Guid?`, required; `Reason: string`, required, max 2,000 chars)
backs a new `OnPostCancelSessionAsync` handler, following the same shape as `SessionResultInput` /
`OnPostCaptureSessionResultAsync` from ADR-0003:

1. `ModelState.Clear()` then `TryValidateModel(SessionCancellation, nameof(SessionCancellation))` — validation
   failure reloads the page and writes nothing;
2. the session is loaded by **posted** `SessionId` and **route** `TaskId` together (`SingleOrDefaultAsync` on both),
   the same cross-task defense ADR-0003 established — a session ID belonging to a different task is indistinguishable
   from an unknown one and returns `404 Not Found` with no writes;
3. a session whose `Status` is not `Started` gets a scoped `SessionCancellation.SessionId` validation error and no
   writes — this makes cancellation replay-safe;
4. one `cancelledUtc` timestamp is captured and used for every field below;
5. exactly one `ContextEntry` is added: `Kind = Handoff`, `Title = "{Role} session cancelled"`, `Content` = the
   trimmed `Reason`, with `ProjectId`/`TaskId`/`SourceSessionId` all derived from the loaded session — never from
   posted values;
6. `session.Status = Cancelled`, `session.CompletedUtc = cancelledUtc`, `task.UpdatedUtc = cancelledUtc`,
   `project.IncrementContextRevision()`;
7. one `SaveChangesAsync` call commits the Handoff entry, the session status/timestamp, the task timestamp, and the
   revision increment together.

`AgentSession.CompletedUtc` is reused as the terminal timestamp for **both** `Completed` and `Cancelled` sessions.
The property name is legacy from when only `Completed` was a terminal status; renaming it would touch the schema for
a cosmetic reason, so Sprint 05 keeps the name and lets `Status` disambiguate which terminal outcome occurred. The
Task Details page reflects this by labeling `CompletedUtc` as plain "Ended" for both terminal statuses instead of
"completed."

The assignment endpoint from ADR-0004 already returns `409 Conflict` for any non-`Started` session, so a cancelled
session's packet becomes unreachable the same way a completed one already was — no endpoint change was needed.

### 3. Cancellation UI

Each `Started` session in the session list gets its own inline cancellation form (hidden `SessionId`, a required
reason textarea, explicit "cannot be undone" wording). `Completed` and `Cancelled` sessions render no cancellation
control. On a validation failure, the reason textarea for the session that was actually submitted re-renders with
the posted value, because `SessionCancellation` is bound the same way `SessionResult` already is and the view only
echoes it back for the session whose ID matches `Model.SessionCancellation.SessionId`. Every existing form
(`StartSession`, `CaptureSessionResult`, `CreateContextEntry`, `UpdateStatus`) keeps validating only its own bound
property, unaffected by the new form's presence.

### 4. Work-queue projection

A new read-only `IWorkQueueService` / `WorkQueueService` (in `Services/`, registered in `Program.cs` alongside
`IContextProjectionService`) queries `FamiliarDbContext` directly — no repository layer — and returns one
`WorkQueueItem` per `FamiliarTask` whose `Status != Completed`:

```text
> 1 Started session      -> NeedsAttention        (StartedSessionCount = N, no ActiveSession)
= 1 Started session      -> ContinueSession        (ActiveSessionId/Role set)
0 sessions                -> StartPlanner
latest terminal Cancelled -> RetryRole             (that session's Role)
latest terminal Completed, role Planner      -> StartImplementer
latest terminal Completed, role Implementer  -> StartReviewer
latest terminal Completed, role Reviewer     -> HumanDecision
```

"Latest terminal" is the `Completed`-or-`Cancelled` session with the greatest `CompletedUtc`, tie-broken by
`StartedUtc` then `Id` — both terminal statuses always have `CompletedUtc` set, so this is well-defined once no
`Started` session remains. A `Cancelled` session never counts as role progress: it only ever produces `RetryRole`
for its own role, and is otherwise skipped when computing "latest completed role" for the next-role transitions.
The action derivation only inspects `AgentSession.Status`, `Role`, `StartedUtc`, and `CompletedUtc` — it never reads
`ContextEntry.Content`, `Summary`, `RawOutput`, or any other AI-authored text, so it cannot infer a Reviewer verdict
or otherwise make a task-completion decision on the application's behalf.

Items are ordered by `TaskUpdatedUtc` descending, then `TaskId` ascending as a stable tie-breaker.

### 5. `/Work` page

A plain Razor Page (`Pages/Work.cshtml[.cs]`, `WorkModel`) lists every item from `GetActiveQueueAsync()`: project,
task, status, next-action label, last update. A `NeedsAttention` row is visually flagged. Rows with a single active
session link to Task Details with `?sessionId=...` so the assignment/capture/cancel controls for that session are
selected on arrival, matching the existing `StartSession` redirect convention from ADR-0004. A "Work queue" link was
added to `_Layout.cshtml`'s primary navigation. The page performs no writes and never mutates task status,
session status, or context revision.

---

## Consequences

### Positive

- A freshly started session's own assignment packet never shows a false stale warning; a genuinely later context
  change still does (unchanged assertion, still covered by `SessionAssignmentMarkdownRendererTests` and
  `SessionAssignmentEndpointTests.Revision_mismatch_renders_stale_context_warning`).
- Abandoning a session now leaves a durable, attributable reason instead of an unexplained `Started` row that
  silently blocks a task forever.
- A user can open `/Work` and get one deterministic instruction per task without reading session history by hand.
- All of this reuses existing enums (`AgentSessionStatus.Cancelled`, `ContextEntryKind.Handoff`) and the existing
  terminal-timestamp column — zero schema change.

### Negative

- The one-active-session invariant is enforced only through the application's own command path; a row inserted
  directly into the database (or by a future second write path) can still violate it. The work queue surfaces this
  as `NeedsAttention` rather than silently repairing it, which is a deliberate trade-off, not an oversight.
- `CompletedUtc` remains a slightly misleading name for a cancellation timestamp. Renaming it is deferred — it would
  be a schema-touching change for a naming concern only.
- The work queue re-derives its state on every request; there is no cached or pushed view. For the current data
  volumes this is a direct, well-indexed EF Core query (`Tasks` keyed by `Status`, `AgentSessions` indexed on
  `(TaskId, StartedUtc)`), so this was not treated as a performance risk worth a projection table.

---

## Alternatives Considered

### Database-level unique constraint (partial unique index) for at most one `Started` session per task

Rejected for this slice. SQLite supports partial unique indexes, but adding one would be a migration, and the
sprint's explicit boundary is "no schema change." The application-level check is sufficient for the current
single-process, single-user usage pattern; concurrency-hard enforcement is deferred, matching the non-goals below.

### A separate `CancelledUtc` column instead of reusing `CompletedUtc`

Rejected — it would require a migration to add a nullable column that is redundant with `CompletedUtc` for every
row that is *not* concurrently both completed and cancelled (impossible, since a session has one terminal status).
`Status` already disambiguates which terminal event happened; a second timestamp column would just be another field
to keep in sync for no additional information.

### Deriving the next action by parsing the latest Review's verdict text

Rejected outright. `ContextEntry.Content` is free-text AI output. Parsing it to decide "Approve" vs. "Request
changes" would make task progression depend on prose formatting a future model revision could silently break, and
would let a worker's own output implicitly complete a task without human sign-off. The queue stops at
`HumanDecision` after a completed Reviewer session and lets a person use the existing `UpdateStatus` form.

### A dedicated `WorkQueueSnapshot` table refreshed on each mutation

Rejected as unnecessary duplication of state that is fully and cheaply derivable from `Tasks` and `AgentSessions` on
read, the same reasoning ADR-0004 applied to assignment packets.

---

## Non-Goals

- A database-level unique constraint or partial index for the one-`Started`-session invariant.
- Optimistic concurrency or multi-process/multi-user claim leasing on session start.
- Calling, authenticating to, or dispatching work to an AI provider or runner API.
- Automatic role dispatch or automatic session creation.
- Parsing a Reviewer's verdict or any other AI-authored prose to drive task status.
- Automatic task completion.
- Immutable context or assignment-packet snapshots.
- New cancellation/retry-attempt tables or an attempt counter.
- Authentication, authorization, CI, deployment, commit, or push.
