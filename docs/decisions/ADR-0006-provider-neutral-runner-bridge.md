# ADR-0006: Provider-Neutral Runner Bridge

**Status:** Accepted

**Date:** 2026-08-03

---

## Context

Every Sprint 03–05 session still requires a human to copy the generated assignment packet
(ADR-0004) into an external AI conversation by hand and then copy the visible response back into
the atomic result-capture form (ADR-0003). That manual relay is the only remaining step between a
Started session and a completed one; it does not autonomously discover work, choose a role, or
decide task completion, but it is tedious and error-prone for the cases where the same trusted
human already knows exactly which session to run through which local tool.

Find Familiar already has everything a machine caller needs: a canonical assignment renderer
(ADR-0004), an atomic capture transaction (ADR-0003), and a lifecycle that enforces at most one
`Started` session per task and rejects replay against a terminal session (ADR-0005). What is
missing is (1) a way for something other than a browser to authenticate and reach that same
capture/cancellation behavior, and (2) a minimal process contract for handing the assignment to an
external tool and getting a structured result back.

---

## Decision

### Explicit invocation, no polling or lease table

A human still starts (or selects) exactly one Started session through the existing Familiar UI.
The runner is then invoked with that session's task ID and session ID as explicit input — it never
discovers, claims, or polls for work. Because the one-`Started`-session-per-task invariant and
terminal-replay rejection already exist (ADR-0005, ADR-0003), no new `Run` or `Lease` entity,
schema migration, claim column, or expiry table is needed: the session itself *is* the unit of
work, and its `Status` *is* the claim.

This also means the runner never decides which role to run next, never auto-starts a session, and
never marks a task `Completed` — all of that remains an explicit human action through the existing
`/Work` queue and Task Details page, unchanged by this sprint.

### Shared atomic application services

`OnPostCaptureSessionResultAsync` and `OnPostCancelSessionAsync` were extracted out of
`Pages/Tasks/Details.cshtml.cs` into two injected services:

- `ISessionResultCaptureService` / `SessionResultCaptureService`
  (`Services/SessionResultCaptureService.cs`) — owns the exact ADR-0003 transaction: field
  validation (required + the existing 12,000/4,000/200 character limits), loading the session by
  both task ID and session ID, requiring `Started`, deriving the artifact kind from the session's
  persisted `Role`, creating exactly four `ContextEntry` rows, completing the session, and saving
  once. It returns a typed `SessionResultCaptureOutcome` (`Success`/`ValidationFailed`/`NotFound`/
  `NotStarted`) instead of throwing or writing to `ModelState`, so both the Razor Page and the
  machine endpoint can map it without parsing exception text or HTML.
- `ISessionCancellationService` / `SessionCancellationService`
  (`Services/SessionCancellationService.cs`) — the same treatment for the ADR-0005 cancellation
  transaction: one bounded reason (max 2,000 characters), one `Handoff` entry, `Cancelled` status,
  one revision increment, one save.

`DetailsModel` now calls these services after its own `TryValidateModel` pass. That preserves the
Razor Page's existing behavior byte-for-byte: posted values still redisplay on validation failure,
and every other manual form (`CreateContextEntry`, `UpdateStatus`) is untouched. But the services
are the *authoritative* gate — they re-validate independently, because the machine API below binds
its request body directly and never goes through `ModelState` at all. A PageModel-only check would
have let a machine caller skip validation entirely.

No repository layer, generic command bus, or mediator package was introduced — these are two plain
injected services over the same `FamiliarDbContext`, matching every existing service in this
codebase.

### Machine authentication boundary

Sprint 06 authenticates only the three new `/api/runner/...` routes. Browser routes, antiforgery,
and the existing lack of a login system are all unchanged — this is explicitly not "the app is now
authenticated," and the app must still not be deployed publicly.

`RunnerBridgeOptions` (`Api/Runner/RunnerBridgeOptions.cs`) binds a single `Token` from
configuration section `RunnerBridge`, which in practice means the environment variable
`RunnerBridge__Token`. It has no default and is never present in committed `appsettings*.json`.

`RunnerBridgeAuthenticationFilter` (`Api/Runner/RunnerBridgeAuthenticationFilter.cs`) is a plain
`IEndpointFilter` applied once to `app.MapGroup("/api/runner")`, so it runs before any of the three
handlers — and therefore before any task/session lookup:

- no configured token → `503 Service Unavailable` with a generic "not configured" message, no
  further processing (so the API is safe to enable this sprint even before deployment secrets
  exist);
- missing/malformed `Authorization` header or a token that doesn't match → `401 Unauthorized`,
  identical body to the "not configured" case's shape but distinct status, and identical regardless
  of whether the requested task/session exists;
- the comparison hashes both the configured and supplied token with SHA-256 and compares the two
  equal-length digests with `CryptographicOperations.FixedTimeEquals`, so neither wall-clock timing
  nor an exception path can leak the configured token's length or contents;
- the filter never logs the `Authorization` header, the configured token, the supplied token, a
  hash of either, or which one failed — only a fixed, non-secret warning string when unconfigured.

No ASP.NET Core authentication/authorization NuGet package was added; a hand-written
`IEndpointFilter` was sufficient for one bearer-token route group and kept the change inside
framework primitives already in the project.

### Versioned JSON contracts

All three endpoints and the adapter stdin/stdout documents share one `ContractVersion = 1`
constant. Property names use the project's existing camelCase HTTP JSON convention
(`ConfigureHttpJsonOptions` in `Program.cs`); the console runner mirrors the same names and casing
independently in `RunnerProtocol.cs` rather than sharing a project reference, so the runner stays a
plain console app with no ASP.NET Core/EF Core dependency.

```text
GET  /api/runner/tasks/{taskId}/sessions/{sessionId}/assignment
  -> { contractVersion, taskId, sessionId, role, contextRevisionRead, rolePrompt, assignmentMarkdown }

POST /api/runner/tasks/{taskId}/sessions/{sessionId}/result
  <- { contractVersion, prompt, rawOutput, summary, artifactTitle, artifactContent }
  -> 204 success / 400 validation / 404 unknown / 409 not Started / 503 unconfigured

POST /api/runner/tasks/{taskId}/sessions/{sessionId}/cancel
  <- { contractVersion, reason }
  -> 204 success / 400 validation / 404 unknown / 409 not Started / 503 unconfigured
```

Task ID and session ID are **route-only** — the result and cancel request bodies carry no ID
fields at all, so there is nothing for a caller to spoof and nothing for the server to "trust or
reject"; identity comes entirely from the authenticated route. This mirrors ADR-0003's existing
rule that provenance is derived server-side, never posted.

The assignment response reuses `SessionAssignmentMarkdownRenderer.RenderRolePrompt` and
`RenderAssignment` verbatim — the same single prompt source ADR-0004 established for the human
packet and the Task Details prefill. There is exactly one place a role prompt is generated, and the
API, the browser packet, and the capture prefill all read it.

Bounds: request bodies are read into a fixed 64 KB buffer (`RunnerContracts.MaxRequestBodyBytes`)
and rejected with `413` the instant that limit is exceeded, before any JSON parsing; assignment
Markdown is capped at 500,000 characters (`RunnerContracts.MaxAssignmentMarkdownLength`), also
`413` if exceeded. Field-level limits on the result body reuse the exact
`SessionResultCaptureService` limits (12,000/4,000/200 characters) because it is the same
underlying validation, invoked through the same service.

The adapter's stdin document (`AdapterInvocation`) carries task ID, session ID, role, the exact
role prompt, and the assignment Markdown — everything needed to do the work and to submit a result
that matches what the server expects. The adapter's stdout document (`AdapterResult`) carries only
`rawOutput`, `summary`, `artifactTitle`, `artifactContent` — the adapter cannot choose a role,
artifact kind, task/session ID, provenance, server URL, or credential; all of those come from the
runner, never from the adapter's output.

### Runner process execution

`FindFamiliar.Runner` (`src/FindFamiliar.Runner`) is a plain `net10.0` console app added to the
solution, with no new NuGet package. It takes `--base-url`, `--task-id`, `--session-id` as
ordinary CLI arguments (none of them secret) and reads three things only from environment
variables, never from a CLI argument: `FAMILIAR_RUNNER_TOKEN` (the Familiar bearer credential),
`FAMILIAR_RUNNER_ADAPTER_PATH` (the administrator-configured adapter executable),
`FAMILIAR_RUNNER_ADAPTER_ARGS` (optional, space-split fixed arguments), and
`FAMILIAR_RUNNER_TIMEOUT_SECONDS` (optional, clamped to `[5, 3600]`, default 300).

`AdapterProcessExecutor` (`AdapterProcessExecutor.cs`) launches the adapter directly:
`ProcessStartInfo` with `UseShellExecute = false` and arguments passed through `ArgumentList`, never
through a formatted/concatenated command string built from task/session/prompt/context content. No
part of Familiar-served content ever becomes a shell token. `FAMILIAR_RUNNER_TOKEN` is explicitly
removed from the child's `ProcessStartInfo.Environment` before `Start()`, so the adapter process
never inherits the Familiar credential even though it inherits the rest of the runner's
environment. Stdout and stderr are read concurrently (`Task.WhenAll`-style, started before the
runner finishes writing stdin) into two independently bounded 256 KB buffers
(`RunnerProtocol.MaxAdapterOutputBytes`), which avoids the classic redirected-pipe deadlock and
caps memory regardless of how much the adapter writes. A timeout (`CancellationTokenSource` linked
to the caller's token) triggers `Process.Kill(entireProcessTree: true)` so a misbehaving adapter
and any children it spawned are actually terminated, not merely abandoned.

`RunnerEngine` (`RunnerEngine.cs`) is the orchestrator, deliberately separated from `Program.cs` so
it can be exercised directly in tests against the real TestServer and the real built
`FindFamiliar.FakeAdapter` executable, without spawning the runner as a second OS process for every
test:

1. fetch and validate the assignment (contract version, task/session ID match, non-blank role
   prompt and Markdown, size bound);
2. build and send the versioned stdin document;
3. run the adapter with the configured timeout;
4. classify the outcome — launch failure, timeout, non-zero exit, oversized stdout, malformed JSON,
   more than one JSON document (checked by confirming no non-whitespace bytes remain after the
   first parsed value), or missing/oversized/blank result fields all count as an adapter failure;
5. on any adapter failure, call the cancellation endpoint with a short fixed reason **category**
   (for example `"Runner cancelled: adapter-timeout."`) — never raw stdout/stderr content — and
   return a distinct exit code for "cancelled, retryable" versus "cancellation itself failed, human
   must recover the Started session";
6. on adapter success, submit the result exactly once, using the exact role prompt the server
   returned in the assignment (not anything the adapter echoed back).

### Failure and replay behavior

Before a result is submitted, every adapter failure path cancels durably and leaves the role
retryable through the normal `/Work` queue (ADR-0005's `RetryRole` action already covers a
`Cancelled` session — no change was needed there). If the cancellation call itself fails (network
error, unexpected status), the runner does **not** retry and does **not** treat the session as
resolved; it exits with a distinct code and the Started session remains visible for a human to
cancel or investigate by hand.

After the result request has been sent, a transport failure (timeout, connection reset) leaves the
actual commit outcome ambiguous — the server may have already completed the session before the
runner's connection dropped. The runner never auto-cancels in this case: an automatic cancellation
after an ambiguous submission could durably cancel a session that Familiar had, in fact, already
completed. Instead it exits with a distinct `ResultSubmissionAmbiguous` code and non-secret
diagnostic text, and leaves reconciliation to a human reading the task's actual state in Familiar.

Replay is enforced the same way ADR-0003/ADR-0005 already enforce it for the browser form: the
capture and cancellation services both require `Started` status, so a second result or cancel call
against an already-`Completed`/`Cancelled` session returns `409 Conflict` and writes nothing. The
runner itself also never loops or retries a result submission automatically — "submit once" is
structural, not just a documented convention.

### Exit codes and logging

`RunnerExitCode` (`RunnerExitCode.cs`) enumerates eight stable, documented categories (`Success`,
`UsageError`, `AssignmentFetchFailed`, `AssignmentInvalid`, `CancelledAfterAdapterFailure`,
`CancellationFailed`, `ResultSubmissionRejected`, `ResultSubmissionAmbiguous`). The runner writes
short diagnostic lines to stderr describing which category occurred and why, but never writes
assignment Markdown, role prompt, raw output, artifact content, adapter stderr content, tokens, or
full HTTP bodies — only field lengths and fixed reason categories, matching the same
non-secret-logging rule the server side follows.

### Deterministic fake adapter

`FindFamiliar.FakeAdapter` (`tests/FindFamiliar.FakeAdapter`) is a second plain console project,
built as an ordinary test-tree project reference so its native apphost executable is copied next to
the test assembly and can be launched exactly like a real adapter — no shell, no `dotnet` prefix.
`FAKE_ADAPTER_MODE` selects one of eight deterministic behaviors (`success`, `nonzero`, `timeout`,
`malformed`, `multiple-json`, `missing-fields`, `oversized`, `stderr-noise`, `echo-env`); every
mode's output is obviously synthetic fixture text and is never presented anywhere as a real AI
response, review, or approval.

### Test strategy

`RunnerProcessEndToEndTests` constructs the real `RunnerEngine` and `AdapterProcessExecutor`
directly, pointed at the shared collection's `WebApplicationFactory` `HttpClient` (the real ASP.NET
Core pipeline — routing, the authentication filter, the endpoints, the shared services, EF Core —
served in-process over `TestServer`'s in-memory handler) and the real built
`FindFamiliar.FakeAdapter` executable, launched as a genuine child OS process. This proves the
whole chain — process launch, stdin delivery, bounded concurrent stdout/stderr draining, timeout
and process-tree termination, JSON contract validation, durable cancellation, and atomic capture —
without requiring a second, separately-listening Kestrel instance in the automated suite. Section 9
of the verification runbook additionally exercises the fully separate `FindFamiliar.Runner`
executable via its real CLI against a live `dotnet run` server, for a true multi-process dogfood
proof; the automated suite's in-process approach is the same trade-off the project already makes
everywhere else (`FindFamiliarWebApplicationFactory` never binds a real socket either).

---

## Consequences

### Positive

- A human can complete a Started session with one runner invocation instead of two manual
  copy/pastes, without introducing any new persistent state.
- The web UI and the machine API are provably the same atomic transaction — a bug fixed in one
  cannot silently remain present in the other, because there is only one implementation.
- The credential boundary is narrow and specific: one bearer token, one route group, fixed-time
  compared, never logged, never forwarded to a child process.
- Every adapter failure mode (timeout, crash, garbage output, oversized output) has a defined,
  tested, durable outcome — nothing is silently dropped or left ambiguous except the one case
  (post-submission transport failure) that is unavoidably ambiguous, and that case is handled by
  deliberately doing nothing unsafe rather than guessing.

### Negative

- The runner is a second thing to keep in sync with the server's contract; `RunnerProtocol.cs` is a
  hand-maintained mirror of `RunnerContracts.cs` rather than a shared assembly. This was a
  deliberate trade-off (see Alternatives) to keep the console app free of ASP.NET Core/EF Core
  dependencies.
- A network failure between "the server committed the result" and "the runner received the 2xx"
  leaves a human needing to check Familiar's actual state before deciding whether to retry — the
  runner cannot resolve that ambiguity for them, by design.
- The one-`Started`-session invariant is still an application-level check, not a database
  constraint (unchanged from ADR-0005); the runner bridge does not add or remove that trade-off.

---

## Alternatives Considered

### Share JSON contract types via a project reference from the runner to the server

Rejected. The console runner would then pull in `Microsoft.NET.Sdk.Web` and EF Core transitively,
turning a five-file console app into something that carries the entire server's dependency graph
for the sake of five small records. Mirroring the contract by hand in `RunnerProtocol.cs`, frozen
by this ADR and covered by end-to-end tests that exercise both sides together, was judged cheaper
than that coupling.

### A `Run`/`Lease` table with claim and expiry

Rejected, per the roadmap's preferred minimal bridge. Explicit human-selected invocation plus the
existing one-`Started`-session invariant already prevents concurrent double-processing of the same
session; a lease table only earns its cost once multiple runners might race for the same work,
which is out of scope for this sprint.

### Auto-cancel on any result-submission failure, including ambiguous ones

Rejected. Treating a network timeout after sending the result the same as a definite rejection
would risk cancelling a session the server had, in fact, already completed — turning a transport
hiccup into data loss (the four captured entries would still exist, but the session's terminal
status and the task's next-action queue would disagree with them). Leaving the ambiguous case to a
human, with a clearly distinct exit code, was judged safer than guessing.

### ASP.NET Core `AddAuthentication`/`AddAuthorization` with a custom scheme

Rejected for this narrow a surface. Three routes behind one bearer token do not need policy
providers, `[Authorize]` attribute wiring, or a scheme handler; a single `IEndpointFilter` applied
to one route group is the smallest correct primitive already available in the framework, and
avoids implying the rest of the application is now behind the same authentication system.

### Let the adapter choose or echo the role/artifact kind/IDs

Rejected, continuing ADR-0003/ADR-0004's existing rule. The adapter's stdout contract has no room
for any of these fields; they are only ever read from the assignment the server generated and the
route the runner was explicitly given.

---

## Non-Goals

- Automatic work discovery, polling, or claiming.
- A `Run`/`Lease` table, expiry, or any new migration.
- A provider SDK or any specific AI vendor integration — the adapter contract is the entire
  integration surface, and `FindFamiliar.FakeAdapter` is the only implementation this repository
  ships.
- Parsing a Reviewer's verdict or otherwise inferring task completion from AI-authored text —
  unchanged from ADR-0005, task status remains an explicit human `UpdateStatus` action.
- Full application authentication, a login system, or any claim that the app is safe to deploy
  publicly.
- Automatic reconciliation of an ambiguous post-submission failure.
- Multi-runner concurrency, queueing, or leasing.
