# ADR-0003: Atomic Session Result Capture

**Status:** Accepted

**Date:** 2026-08-02

---

## Context

Finishing a Started agent session previously required at least five separate submissions on the Task Details page: record `Prompt`, record `RawOutput`, record `Summary`, record the role artifact (`Plan`, `Implementation`, or `Review`), then click a separate **Complete** button.

Five independent form posts against the same session create room for partial results: a user can record some entries and abandon the rest, complete a session before its artifact is recorded, or record an artifact of the wrong kind because the form let them pick any `ContextEntryKind`. None of these failure modes are visible until a later session reads incomplete or mismatched context.

---

## Decision

Add one atomic command, `OnPostCaptureSessionResultAsync`, to the existing Task Details `PageModel`. A single validated form submission:

1. loads the targeted `AgentSession` by both the posted session ID and the route's task ID, so a session belonging to a sibling task is rejected;
2. requires the session's persisted status to be `Started`;
3. derives the result's artifact kind from the session's persisted `Role` — never from posted input:
   - `Planner` → `Plan`
   - `Implementer` → `Implementation`
   - `Reviewer` → `Review`
4. creates exactly four `ContextEntry` rows — `Prompt`, `RawOutput`, `Summary`, and the mapped artifact — all `Active`, all carrying the project/task/source-session IDs read from the loaded entities;
5. sets the session to `Completed` and stamps `CompletedUtc`;
6. sets the task's `UpdatedUtc` and increments the project's `ContextRevision` exactly once;
7. commits everything through one `SaveChangesAsync` call.

Validation failures, a cross-task session ID, an oversized field, or a replayed submission against an already-`Completed` session all return the page with no database writes — EF Core's single save unit makes the whole command succeed or fail together.

The Task Details Razor Page renders one result form whenever at least one session is `Started`; its session selector lists only `Started` sessions and preselects the session identified by the `sessionId` query value left by the `StartSession` redirect. The normal UI no longer offers a standalone **Complete** button, and no legacy completion handler remains — completion now happens through result capture.

`OnPostCreateContextEntryAsync` is changed to scoped validation (`ModelState.Clear()` + `TryValidateModel(NewContextEntry, ...)`), matching the pattern already used by `OnPostStartSessionAsync` and the Project Details handlers. Without this change, adding a second required, page-bound model (`SessionResult`) would make the page-wide `ModelState.IsValid` check the context-entry handler relied on fail whenever the sibling result form's required fields were unset.

No new table, migration, or repository layer is introduced. The result bundle is still four ordinary `ContextEntry` rows differentiated by `Kind`.

---

## Consequences

### Positive

- A session can only be completed together with its full result bundle — completion and an incomplete record can no longer diverge.
- The artifact kind can no longer be mismatched to the session's role, because it is never client-supplied.
- Fewer form submissions means fewer chances to abandon a session mid-handoff.
- The manual context-entry form remains available, unchanged in behavior, for human knowledge and one-off exceptions that don't belong to any session.

### Negative

- The result form's four content fields are still ordinary bounded text; nothing prevents a user from recording an inaccurate summary or artifact.
- `RawOutput` remains a single bounded excerpt (max 12,000 characters), not a full transcript — this command does not change that constraint, only makes recording it atomic with the rest of the bundle.
- Task completion is intentionally not automatic: a Reviewer's `Approve` verdict still requires a separate `UpdateStatus` submission.

---

## Alternatives Considered

### Keep five independent submissions, add a "resume" indicator

Rejected because it treats the symptom (users losing track of a partially recorded session) rather than the cause (five uncoordinated writes with no shared transaction).

### Add a `SessionResult` table populated first, then materialize `ContextEntry` rows from it

Rejected as unnecessary duplication — the four `ContextEntry` rows already are the canonical durable record per ADR-0002; an intermediate table would just be another copy to keep in sync.

### Let the client choose the artifact kind

Rejected because the session's `Role` already determines the only sensible artifact kind; accepting a posted kind would let a client record a `Review` entry against an `Implementer` session.

---

## Bootstrap Note

Sprint 03 itself had to be planned before this capture form existed. Its Planner session is recorded with the pre-existing manual context-entry and completion controls as a one-time bootstrap exception; the Implementer and Reviewer sessions for this same task are recorded through the new atomic form described above.
