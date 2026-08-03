# ADR-0004: Session Assignment Packets

**Status:** Accepted

**Date:** 2026-08-02

---

## Context

Starting a Planner, Implementer, or Reviewer session records an `AgentSession` row, but Sprint 03 left the user to
assemble the actual handoff by hand: fetch or copy the current `context.md`, then find and append a separate role
prompt from outside the application. Nothing ties the copied context to the specific session that was just started,
so the wrong role prompt, the wrong task, the wrong session ID, or a stale context revision can be handed to a
worker without the application ever noticing.

Find Familiar already knows every fact needed to build a complete, correct handoff: the session's persisted role,
its `ContextRevisionRead`, the project's current `ContextRevision`, and the canonical task context via
`ContextProjectionService` and `MarkdownContextRenderer`. Manual assembly is unnecessary duplication of data the
server already has.

---

## Decision

Add one read-only endpoint:

```text
GET /tasks/{taskId:guid}/sessions/{sessionId:guid}/assignment.md
```

It loads the task's canonical `TaskContextDocument` through the existing `IContextProjectionService`, selects the
session only from that document's session list (so a session ID belonging to a different task is indistinguishable
from an unknown session ID), and returns:

- `404 Not Found` when the task does not exist;
- `404 Not Found` when the session ID is unknown or belongs to a different task;
- `409 Conflict` when the session is not `Started`;
- `200 OK` with `text/markdown; charset=utf-8` for a valid Started session.

The endpoint performs no database writes and never calls `SaveChangesAsync` — it only reads through the projection
service already used by `context.md` and `context.json`.

### One role-prompt source

`Services/SessionAssignmentMarkdownRenderer.cs` is a new static class, following the `MarkdownContextRenderer`
convention, that owns:

- `RenderRolePrompt(AgentSessionRole role, TaskContextDocument document)` — the exact prompt text for a role, given
  the task it applies to. It exhaustively switches over `AgentSessionRole` (Planner, Implementer, Reviewer) and
  throws for anything unmapped, matching the pattern already used in `OnPostCaptureSessionResultAsync`'s artifact-kind
  mapping.
- `RenderAssignment(TaskContextDocument document, AgentSessionDocument session)` — the complete packet, which calls
  `RenderRolePrompt` internally and reuses `MarkdownContextRenderer.Render` verbatim for the canonical context
  section, rather than re-implementing entry ordering or provenance rules.

The Task Details `PageModel` calls `RenderRolePrompt` from the same class to prefill an empty `SessionResult.Prompt`
on a normal `GET` for the session identified by the `sessionId` query value, whenever that session is `Started`.
This call only happens inside `OnGetAsync`; the `OnPost*` handlers that redisplay the page after a validation failure
call `LoadContextAsync` directly and never touch `SessionResult`, so a posted `Prompt` value is never overwritten by
a freshly generated one.

Role contracts, matching `01-current-state-and-architecture.md`:

- **Planner** — inspect and plan, no edits, return a Plan artifact.
- **Implementer** — inspect, implement within recorded constraints, verify, return an Implementation artifact.
- **Reviewer** — independently inspect and verify, no edits, return an explicit Approve/Request-changes verdict in a
  Review artifact.

Every role prompt asks for a concise Summary, an artifact title, and artifact content — the same shape
`SessionResultInput` already captures. No role or artifact kind is ever accepted from the request; it is derived
solely from the session's persisted `Role`, the same rule ADR-0003 established for result capture.

### Packet contents and ordering

`RenderAssignment` produces, in order:

1. `# Find Familiar assignment` title;
2. an **Execution contract** section with project name/ID, task title/ID, requested outcome, session ID, persisted
   role, provider/external reference (or explicit "Unspecified"/"None"), session status, `ContextRevisionRead`, and
   the current project `ContextRevision`;
3. a prominent `STALE CONTEXT WARNING` blockquote when `ContextRevisionRead` differs from the current revision,
   instructing the reader to stop and start a fresh session — the endpoint does not rewrite `ContextRevisionRead` or
   otherwise react to the mismatch itself;
4. an **Exact role prompt** section containing the same text `RenderRolePrompt` produces;
5. a **Required result** section describing the Summary/artifact-title/artifact-content shape and stating that the
   visible response becomes bounded `RawOutput`, not a full transcript;
6. a **Canonical task context** section containing `MarkdownContextRenderer.Render(document)` unmodified.

### Task-page integration

The Task Details page shows an "Open assignment packet" link next to every session whose status is `Started`
(and no link for `Completed`/`Cancelled` sessions). When the page is loaded with the `sessionId` query value left by
the `StartSession` redirect, a prominent callout surfaces the same link so the just-started session's handoff is the
obvious next action. The existing atomic capture form (ADR-0003) and the manual context-entry form are otherwise
unchanged.

---

## Consequences

### Positive

- A worker can be handed one URL instead of two manually assembled artifacts, eliminating role/task/session/revision
  mix-ups.
- The role prompt has exactly one source of truth, used identically by the packet and by the result-form prefill —
  they cannot drift apart.
- The endpoint adds no new persistence, so there is nothing to keep in sync with `ContextEntry`/`AgentSession`.
- Ownership and status checks reuse the same "load via the task's own document" pattern already proven in
  `OnPostCaptureSessionResultAsync`, closing the cross-task-ID hole the same way.

### Negative

- The packet is generated fresh on every request; it is not an immutable snapshot. If context changes between
  opening the packet and giving it to a worker, only the visible warning — not an enforced block — protects against
  acting on stale context. Enforcing that belongs to Sprint 05's lifecycle work.
- Nothing prevents copying the packet into more than one worker conversation; the endpoint does not mark anything as
  "claimed." That is explicit non-goal, deferred to the runner-bridge work in Sprint 06.

---

## Alternatives Considered

### Store a generated snapshot as a new `AssignmentPacket` entity

Rejected as unnecessary duplication — the packet is fully derivable from the existing `TaskContextDocument` and
`AgentSession` on every request; persisting a copy would just be another representation to keep consistent with the
canonical context, which ADR-0002 already established as the single source of truth.

### Let the client specify which role prompt to render

Rejected because the session's persisted `Role` already determines the only correct prompt and artifact kind,
mirroring the reasoning ADR-0003 already applied to artifact-kind derivation. Accepting a posted role would let a
client request Implementer instructions for a Planner session.

### Silently rewrite `ContextRevisionRead` when stale

Rejected — the endpoint is read-only by design, and rewriting a session's read revision from a `GET` request would
be a hidden mutation with lifecycle implications (effectively an implicit "re-read context" action) that belongs to
explicit session-lifecycle handling in Sprint 05, not to packet generation.

---

## Non-Goals

- Calling or authenticating to an AI provider.
- Automatic dispatch, polling, streaming, or callbacks.
- Immutable assignment snapshots or a new assignment table.
- Storing full transcripts or hidden reasoning.
- Parsing agent output automatically.
- Changing the Sprint 03 capture transaction.
- Concurrency or optimistic-lock enforcement on the stale-context warning.
- Cancel/reopen/abandon session lifecycle.
- Task-status automation.
- Authentication, authorization, CI, deployment, commit, or push.
