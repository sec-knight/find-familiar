# Conversational Familiar v0 — Acceptance Checklist

What must be true before this is accepted. Every box is a fact someone can verify, not an intention.

---

## Build and hygiene

- [ ] `dotnet build` — 0 warnings, 0 errors.
- [ ] `dotnet test` — all pass, three consecutive full runs on the branch, plus one from merged `main`.
- [ ] `dotnet ef migrations has-pending-model-changes` — no pending model changes.
- [ ] `git diff --check main...HEAD` — clean.
- [ ] No credential, host, API key, or machine-local path in the diff, except synthetic fixtures whose
      purpose is to prove redaction.
- [ ] History is linear; no merge commits.

## Route and page

- [ ] `/Familiar/{projectId:guid}` renders for an existing project.
- [ ] Unknown or malformed project id returns 404.
- [ ] Project identity, purpose, status and context revision are shown.
- [ ] Links to `/Projects/Details/{id}` and `/Demiplane/{id}` are present, and the Demiplane links back.
- [ ] The deterministic summary renders with **no provider configured**.
- [ ] "What I can't see" (snapshot limitations) is always visible, not behind a disclosure control.
- [ ] Proposed actions render in their own labelled region, outside message bubbles.
- [ ] Every Familiar message shows its provider and model.
- [ ] The page has no meta refresh and no required JavaScript.
- [ ] An archived project renders read-only with the input disabled and the reason stated.

## Schema

- [ ] One migration, `FamiliarConversations`. No existing table altered and no existing row touched.
- [ ] Unique index on `FamiliarConversations.ProjectId`.
- [ ] Unique index on `(ConversationId, Sequence)` for messages.
- [ ] Unique filtered index `IX_FamiliarActionProposals_ConversationId_Pending`, and a guard test that
      fails if the enum's string conversion is removed.
- [ ] Unique filtered indexes on `CreatedTaskId` and `CreatedSessionId`, both `Restrict`.
- [ ] Enums stored as strings with `HasMaxLength(32)`.
- [ ] No column exists anywhere for prompts, thinking, or raw provider payloads.

## Snapshot

- [ ] `IProjectSnapshotService` performs no writes and calls no provider.
- [ ] Every query is filtered by `projectId`.
- [ ] Caps are `public const` and match `specification.md` §4.1: 20 tasks, 10 sessions, 10 handoffs,
      15 context entries, 500-char entry excerpts, 1 000-char purpose.
- [ ] Ordering matches the Demiplane's for tasks; sessions and entries are recency-ordered.
- [ ] Worker keys and display names are absent from the snapshot.
- [ ] Every truncation produces a corresponding `Limitations` line.
- [ ] `MaxSnapshotCharacters` is enforced; the drop order is tested at the boundary.
- [ ] A project that exceeds the budget after the floor sets `IsWithinBudget = false` and is never sent.

## Reasoning provider

- [ ] `IFamiliarReasoningProvider` and its records name no specific provider.
- [ ] `RespondAsync` never throws — verified by a test that scripts an exception from the SDK layer.
- [ ] Every `FamiliarReasoningStatus` is mapped to page wording.
- [ ] `UnconfiguredFamiliarReasoningProvider` is the default registration; the application starts and
      the page works with no credential present.
- [ ] The Claude implementation lives in its own project; `FindFamiliar.Server` does not reference the
      `Anthropic` package.
- [ ] Model, max tokens, effort and timeout are configurable; the API key is environment-only.
- [ ] No tools are declared on the provider call.
- [ ] `stop_reason == "refusal"` is checked before `Content` is read.
- [ ] Timeout is enforced by a caller-owned `CancellationTokenSource`.
- [ ] No test makes a network call or requires a credential.

## Behaviour contract

- [ ] `FamiliarBehaviorContract.Text` is the only copy; no duplicate in a document or a `.txt` asset.
- [ ] Tests assert its load-bearing properties: three registers (recorded / inferred / unknown), no
      URLs or commands, at most three recommendations, no claim that an action occurred, explicit
      statement that unlisted requests cannot be performed.

## Actions

- [ ] Exactly two kinds: `CreateTask` and `StartPlanner`. No third exists in the enum.
- [ ] Each proposal shows what will happen, why, its parameters, what state will change, and Confirm
      and Dismiss controls.
- [ ] `CreateTask` parameters are editable by the human before confirming.
- [ ] `StartPlanner` targets are selected from this project's tasks; no free-text id path exists.
- [ ] Confirmation performs a conditional consume of the `Pending` row by token before any effect.
- [ ] All effects of a confirmation commit in one transaction, or none do.
- [ ] Every gate is re-validated inside the transaction.
- [ ] `CreateTask` is revision-gated; `StartPlanner` is not, and a comment says why.
- [ ] Execution delegates to `IWorkflowDispatchService`; no business logic is duplicated.
- [ ] Durable `CreatedTaskId` / `CreatedSessionId` links are written after the rows exist.

## Failure states

- [ ] All eight failure codes in `user-experience.md` §3 exist, are asserted verbatim, and appear on
      the page.
- [ ] Every failure leaves the page fully usable, with the deterministic summary intact.
- [ ] The human message is durable before the provider call — verified by a test that fails the
      provider after the first commit.
- [ ] Stale and invalid proposals replace Confirm with a specific reason, derived at render time.
- [ ] Confirmation outcome wording matches `user-experience.md` §4 exactly.
- [ ] SQLite busy and locked conditions classify as `DatabaseBusy` on every path, including
      transaction acquisition and rollback, and are never reported as a lost race.
- [ ] No generic fault claims a competing actor.

## Security

- [ ] Every write is a Razor Page POST handler with antiforgery; no minimal API write endpoint exists.
- [ ] `GET` writes nothing — asserted by a row-count test across a page load.
- [ ] Model text is rendered encoded; no `Html.Raw`, no markdown-to-HTML, no autolinking.
- [ ] A model-authored URL is not clickable — asserted by a test.
- [ ] User message capped at 4 000 characters; history capped at 10 messages.
- [ ] Provider `Detail` containing a fake credential and a fake path is proven not to reach the page or
      the database.
- [ ] A proposal belonging to another project cannot be confirmed from this page.
- [ ] Evidence ids not present in the originating snapshot are dropped.

## Required tests

Each must exist and be meaningful.

- [ ] `ProjectSnapshotServiceTests` — project isolation; a second project's tasks, sessions, handoffs
      and context entries never appear.
- [ ] `ProjectSnapshotServiceTests` — cap and ordering correctness at each boundary.
- [ ] `ProjectSnapshotServiceTests` — every truncation emits a limitation.
- [ ] `ProjectSnapshotServiceTests` — over-budget project is refused, not truncated silently.
- [ ] `ProjectSnapshotServiceTests` — absence is not an event: a project with no handoffs produces no
      statement about a decision.
- [ ] `FamiliarSummaryWriterTests` — no sentence without a supporting non-null field.
- [ ] `FamiliarConversationServiceTests` — human message durable before the provider call.
- [ ] `FamiliarConversationServiceTests` — each provider failure status produces the right system
      message and writes nothing else.
- [ ] `FamiliarConversationServiceTests` — provider `Detail` is sanitised.
- [ ] `ProposedActionValidatorTests` — cross-project task rejected; unknown kind rejected; empty title
      rejected; over-length rejected; rejection is silent.
- [ ] `FamiliarActionServiceTests` — confirmation requires the current token; a stale token changes
      nothing.
- [ ] `FamiliarActionServiceTests` — concurrent confirmation produces exactly one task/session and one
      truthful loser.
- [ ] `FamiliarActionServiceTests` — replayed confirmation is idempotent and returns the original links.
- [ ] `FamiliarActionServiceTests` — `StartPlanner` on a task with a `Started` session is rejected.
- [ ] `FamiliarActionServiceTests` — inactive project rejected; revision gate enforced for `CreateTask`.
- [ ] `FamiliarActionServiceTests` — database-busy classification on every path.
- [ ] `FamiliarActionServiceTests` — provider output cannot reach `IWorkflowDispatchService` without a
      confirmed proposal.
- [ ] `FamiliarPageTests` — 404 for unknown project; GET mutates nothing; antiforgery required on every
      POST; model-authored URL not rendered as a link.
- [ ] `FamiliarPageTests` — page renders and is useful with the unconfigured provider.
- [ ] `FamiliarBehaviorContractTests` — contract properties.
- [ ] `FamiliarFailureWordingTests` — every status has wording; no wording names a host, path, or
      exception.
- [ ] `FamiliarConversationMigrationTests` — schema applies to a fresh database and to one at the
      Sprint 10 baseline.
- [ ] `FamiliarProposalPendingUniqueIndexTests` — the filtered index survives, and fails loudly if the
      enum conversion changes.
- [ ] `FamiliarDemiplaneConsistencyTests` — the deterministic summary and the Demiplane never disagree
      about a task's state.

## Documentation

- [ ] `docs/decisions/ADR-0012-conversational-reasoning-and-human-confirmed-action.md` exists, is
      marked Accepted, and matches the shipped code.
- [ ] `docs/familiar-guide.md` exists: what the page does, how to configure a provider, what each
      failure means, and an explicit statement that the Familiar cannot act without confirmation.
- [ ] `README.md` roadmap updated.
- [ ] `docs/sprint-acceptance.md` records the accepted commit, test counts, what was verified, and what
      the sprint established.

## Manual verification at acceptance

- [ ] Ran with no provider configured; the page is genuinely useful.
- [ ] Ran with the fake provider through every failure path.
- [ ] Confirmed one `CreateTask` and one `StartPlanner`; verified in the database that the created work
      is indistinguishable from manually created work.
- [ ] Replayed a confirmation POST; exactly one row was created.
- [ ] Read a full transcript looking for the specific failure this feature risks: confidence about
      something the data does not support. Found none.
