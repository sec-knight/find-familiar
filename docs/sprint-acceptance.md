# Sprint acceptance record

A durable record of which sprints have been accepted, what was accepted, and what was verified at the
time. Git history, migrations, tests and ADRs remain the source of truth; this file is the index that
says which point in that history was signed off, and on what evidence.

---

## Sprint 10 — The Demiplane

**Accepted 2026-08-05.**

| | |
|---|---|
| Accepted commit | `de035ee770bfa48a700cbef8b4a95b0277a0e5d4` — *Say only what the data supports, and classify locks on every path* |
| Review range | `6f6e41d..de035ee` (5 commits) |
| Merge into `main` | Fast-forward from `fc16678`. The repository has no merge commits; history stays linear, so there is no merge commit to cite — final `main` head **is** the accepted commit. |
| Final `main` head at acceptance | `de035ee770bfa48a700cbef8b4a95b0277a0e5d4`. `main` advances past the tag by this documentation-only commit; the accepted code baseline remains `de035ee`. |
| Tag | `demiplane-baseline-v0` (annotated) → `de035ee` |
| Tests | **523 passed, 0 failed, 0 skipped** — three consecutive full runs on the branch, plus a fourth from merged `main` |
| Final review result | **PASS** — no blocking findings |

### What was verified

- `dotnet build` — succeeded, 0 warnings, 0 errors.
- `dotnet test` — 523/523, four times (three on the branch, once from merged `main`).
- `dotnet ef migrations has-pending-model-changes` — no model changes since the last migration.
- `git diff --check 6f6e41d HEAD` — clean, no whitespace errors.
- Secret scan and machine-path scan of the range — no credentials and no machine-local paths. The
  only matches are synthetic fixtures in `ProviderCapacityServiceTests`, which exist precisely to
  assert a reader's exception text cannot put a token or a local path on screen.
- Merge topology — strictly linear; every Sprint 05–10 commit present in order, Sprint 09's
  `20260805011808_SessionHandoffsAndStartedSessionUniqueness` migration included, no accepted commit
  stranded on another branch, no unrelated experimental commits.

### What the sprint established

The Demiplane is a per-project command surface that states only what the persisted data supports.
The rules behind it are recorded in [ADR-0011](decisions/ADR-0011-task-display-state-and-provider-capacity.md):

- Display state is derived in one place and is never execution authority.
- Absence of a handoff row is never reported as a human decision — `NoNextStepProposed` and
  `ProposedStepDeclined` are distinct, and decisions stay scoped to the latest terminal session's
  `SourceSessionId`.
- Failure categories come only from the fixed diagnostic strings this codebase's own runner writes.
  Nothing is inferred from model-authored text.
- SQLite busy and locked conditions are classified as `DatabaseBusy` on every path — including while
  acquiring a transaction and while rolling one back — and are never reported as a lost race. Generic
  faults claim no competing actor; only outcomes that establish a real competitor use race wording.
- No provider-capacity exhaustion is claimed. The shipped reader reports `Unknown` and says why.
- Viewing and refreshing the Demiplane mutates nothing. Every mutation is POST-only with antiforgery.

### Database reset

The development SQLite database was **intentionally deleted and rebuilt from migrations** on
2026-08-05. This environment is a development sandbox and its database held disposable data; a local
archive was taken outside the repository and neither it nor the database is committed.

Rebuilding from empty confirmed: all four migrations apply cleanly, `SessionHandoffs` and all 21
expected indexes exist (including `IX_AgentSessions_TaskId_Started` and
`IX_SessionHandoffs_TaskId_Pending`), no migrations remain pending, and no seed or startup process
creates historical state — every table starts empty.

### Operational baseline

Find Familiar now has an operational Demiplane baseline. The project **Find Familiar** was created on
the fresh database and its Demiplane verified end to end: Observatory, Waiting for you, Scrying pool
and Work map all render; "Waiting for you" is accurately empty; the Scrying pool reports Claude as
`Unknown` with its reason rather than inventing a number; five consecutive refreshes changed no row
and did not move `ContextRevision`. No live provider was invoked.

### Known follow-up (not a Sprint 10 blocker)

`StaleContext` may be returned to a losing concurrent approval when a winner commits between
preflight reads. `WorkApprovalService.ApproveAsync` reads the proposal and the project in two separate
statements; a contender that reads the proposal while it is still Pending and then reads the project
after the winner's commit has advanced `ContextRevision` is told its context went stale, when what
actually happened is that someone else approved.

Nothing is created either way and the loser carries no task or session links, so no guarantee is
broken — the message simply points at a refresh rather than at the approval that won. The fix belongs
to a later sprint: on a stale-context preflight, re-read the proposal and return `AlreadyApproved`
when it has become terminal, before reporting `StaleContext`. Recorded in ADR-0011.
