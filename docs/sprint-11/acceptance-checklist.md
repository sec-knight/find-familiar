# Sprint 11 — Acceptance checklist

Sprint 11 has two deliverables and both must pass. The code change is small; the lifecycle is the
point. A green test suite with a fabricated lifecycle is a failed sprint.

Nothing here may be marked done on inference. Each line is either verified by a command whose output
you saw, or by a database row you read, or by looking at a screen. Anything else is `not verified`.

---

## A. The code change

| | Check | Evidence |
|---|---|---|
| A1 | Work map task anchor href ends `#task-detail`, `taskId` intact | Automated test |
| A2 | Waiting-for-you task anchor href ends `#task-detail` | Automated test |
| A3 | `<section id="task-detail">` still exists with that exact id | Automated test |
| A4 | `Selecting_a_task_reveals_the_familiar_summary` passes unmodified | Test run |
| A5 | Approve redirect `Location` carries **no** `#task-detail` | Automated test |
| A6 | Decline redirect `Location` carries **no** `#task-detail` | Automated test |
| A7 | `site.css` sets `scroll-margin-top` on `#task-detail` | Diff |
| A8 | A page with tasks and no `taskId` renders no detail panel and no error | Automated test |
| A9 | `dotnet build` — 0 warnings, 0 errors | Command output |
| A10 | `dotnet test` — 523 prior tests pass, plus the new ones; 0 failed, 0 skipped | Command output |
| A11 | `dotnet ef migrations has-pending-model-changes` — none | Command output |
| A12 | Diff touches only `Pages/Demiplane.cshtml`, `wwwroot/css/site.css`, `Http/DemiplanePageTests.cs` | `git diff --stat` |
| A13 | No JavaScript added anywhere | Diff |
| A14 | No migration, model, service, or projection change | Diff |
| A15 | `git diff --check` clean | Command output |
| A16 | No credential, token, or machine-local path in the diff | Secret scan of the range |

## B. Visual verification — human, on a real phone

Automated tests cannot assert that a person can see the thing. Do these by hand.

| | Check | Result |
|---|---|---|
| B1 | Demiplane open on a phone (or a 390px viewport) for a project with ≥3 tasks | |
| B2 | Tapping a task in the Work map puts the detail on screen **without scrolling** | |
| B3 | A small gap sits above the panel; it is not flush to the viewport edge | |
| B4 | Scrolling up from the panel still reaches Observatory and Waiting for you | |
| B5 | Tapping a task in Waiting for you behaves the same | |
| B6 | After Approve or Decline, the outcome message is visible on landing, at the top | |
| B7 | The selected card's `is-selected` outline is visible when scrolled back up | |

## C. The lifecycle

This is the sprint's real claim. Each line asks for a persisted record, not a recollection.

| | Check | Evidence |
|---|---|---|
| C1 | A real Sprint 11 task exists under project `Find Familiar` | Task ID |
| C2 | Its purpose and acceptance criteria are recorded and match the specification | Task record / context entries |
| C3 | A Planner session ran and its result was captured | Session ID, `Status = Completed` |
| C4 | A `SessionHandoff` proposing Implementer was staged by the application | Handoff ID, `Status = Pending` |
| C5 | It appeared under **Waiting for you** on the Demiplane | Screen |
| C6 | The **user** approved it through the Demiplane | Handoff `Status = Approved`, `CreatedSessionId` set |
| C7 | A worker claimed the Implementer session | Worker ID, claim record |
| C8 | The worker performed the change in a clean linked worktree | Working tree diff |
| C9 | The Implementer's outcome was captured | Session ID, `Status = Completed` |
| C10 | A Reviewer step was proposed, approved, claimed and completed | Handoff and session IDs |
| C11 | The chain is legible on the Work map without opening a terminal | Screen |
| C12 | The task detail explains the whole story to someone who was not present | Screen |
| C13 | The resulting code was independently reviewed before merge | Review verdict |
| C14 | The task was completed **by a human**, not by the system | Task `Status`, and who set it |

## D. Honesty

The checks that decide whether this sprint proved anything.

| | Check | Result |
|---|---|---|
| D1 | Every step performed by hand is identified as such, in writing | |
| D2 | No step performed outside Find Familiar is described as Find Familiar having done it | |
| D3 | Every session in the record corresponds to a session that actually ran | |
| D4 | No fabricated completed state was written directly to the database | |
| D5 | Any live provider invocation was preceded by explicit user approval | |
| D6 | Workflow gaps found during the cycle are written down, including ones that did not block | |
| D7 | Claims that could not be verified are stated as unverified | |

**D2 is the blocking one.** If the record implies the application managed a step a human performed,
the sprint fails regardless of the state of the code.

## E. Merge

| | Check | Evidence |
|---|---|---|
| E1 | Work happened on a branch, not on `main` | `git branch -vv` |
| E2 | `main` was not modified during the sprint | `git log main` |
| E3 | Independent review completed with a recorded verdict | Reviewer report |
| E4 | Full suite green from merged `main` | Command output |
| E5 | `docs/sprint-acceptance.md` updated with commit, range, tag, counts, and what was verified | Diff |
| E6 | Workflow gaps recorded in the acceptance record or an ADR | Diff |

---

## Sign-off

Sprint 11 is accepted only when A, B, C, D and E are all satisfied, or when a deviation is written down
with its reason and accepted deliberately.

| Field | Value |
|---|---|
| Accepted commit | |
| Review range | |
| Tag | |
| Test counts | |
| Final review result | |
| Deviations accepted | |
