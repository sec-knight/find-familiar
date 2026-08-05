# Conversational Familiar v0 — Implementation Prompt

You are implementing the first human-facing Familiar in Find Familiar. Read
[`specification.md`](specification.md), [`architecture.md`](architecture.md) and
[`user-experience.md`](user-experience.md) in full before writing code. They are the brief; this
document is how to execute it.

This is **Sprint 11**. The branch is **`feature/sprint-11-conversational-familiar-v0`**, taken from
`main` at `5d7b13e` — the Sprint 10 Demiplane baseline plus the README roadmap update.

---

## The one thing that must not go wrong

There must be no code path by which text from a reasoning provider changes persisted state without
(a) an explicit human confirmation click and (b) a re-validation inside the confirming transaction.

Everything else in this sprint is ordinary work. That is the invariant. Before every commit, ask
whether the diff opened a path around it.

## Ground rules

1. **Do not modify existing behaviour.** No changes to `Conversation`, `WorkProposal`,
   `SessionHandoff`, the Runner bridge, the adapter, the claim path, or `DemiplaneProjectionService`'s
   rules. If you believe one must change, stop and say why rather than changing it.
2. **Reuse the boundaries that exist.** `IWorkflowDispatchService` for task and session creation.
   `IDemiplaneProjectionService` for task display state. `SessionHandoffApprovalService.IsDatabaseBusy`
   for SQLite classification. Do not re-derive a rule that already has one home.
3. **Never persist hidden reasoning.** Only user-visible text, structured evidence ids, proposals and
   operational metadata. No prompts, no thinking, no raw payloads — not in the database, not in logs.
4. **No secret, path, host, or exception text may reach a page or a log.** Provider failures become
   fixed codes from `FamiliarFailureWording`.
5. **Comment the way this repository does.** The existing services explain *why a rule exists and what
   the alternative would have broken*. Match that density and that register. Do not narrate what the
   next line does.
6. **Every state-changing endpoint is a Razor Page `OnPost*` handler** with antiforgery. No minimal API
   write endpoints.

## Slice order

Work the six slices from `architecture.md` §8 in order. Each slice ends with a green full suite
(`dotnet build` clean, `dotnet test` all-pass) and a self-contained commit. Do not begin a slice until
the previous one is green.

### Slice 1 — Snapshot

`ProjectSnapshot` and `IProjectSnapshotService`, built over `IDemiplaneProjectionService`.

- Every query filters on `projectId`. Prove it by writing the isolation test first.
- The caps and ordering in `specification.md` §4.1 are exact. Put them on the record as `public const`
  so tests assert the constant, not a magic number.
- `Limitations` are composed deterministically from what was actually dropped. A truncation that
  produces no limitation line is a defect.
- `EstimatedCharacters` and the drop order are part of the contract; test them at the boundary.
- `FamiliarSummaryWriter` in the same slice, following `FamiliarSummaryComposer`'s discipline: a
  sentence exists only when the field supporting it is non-null.

### Slice 2 — Schema

Four entities, `FamiliarDbContext` configuration, one migration named `FamiliarConversations`.

- String-converted enums with `HasMaxLength(32)`, matching every other enum in this context.
- Unique index on `FamiliarConversations.ProjectId`.
- Unique index on `(ConversationId, Sequence)` for messages.
- Unique **filtered** index `IX_FamiliarActionProposals_ConversationId_Pending` on `ConversationId`,
  filter `"Status" = 'Pending'`. This filter matches the stored TEXT because of the string conversion.
  `AgentSessionStartedUniqueIndexTests` exists because that coupling can silently vanish — write the
  equivalent guard test here.
- Unique filtered indexes on `CreatedTaskId` and `CreatedSessionId` where not null, with
  `DeleteBehavior.Restrict` on both: a proposal that claims to have created a session must never have
  that session deleted out from under the claim.
- Cascade from project and conversation; restrict from task.
- Add a migration test in the shape of `SessionHandoffMigrationTests`.
- Verify `dotnet ef migrations has-pending-model-changes` is clean before committing.

### Slice 3 — Read-only page

`/Familiar/{projectId:guid}`, `@page "/Familiar/{projectId:guid}"`, matching `Demiplane.cshtml`.

- `GET` performs no writes. Assert this with a test that snapshots row counts across a `GET`.
- Unknown project ⇒ `NotFound()`.
- Identity, deterministic summary, always-visible limitations, links to project and Demiplane.
- Add the reciprocal link from the Demiplane to the Familiar page.
- No meta refresh.

### Slice 4 — Conversation and the provider abstraction

- `IFamiliarReasoningProvider` and its records in `Services/Familiar/Reasoning/`. Nothing in this
  namespace may name Claude.
- `UnconfiguredFamiliarReasoningProvider` is the **default** DI registration.
- `FamiliarBehaviorContract.Text` — one copy, in code. Write it against the intent in
  `specification.md` §6 and add a test asserting the properties that matter (it forbids URLs and
  commands, it caps recommendations, it requires the three registers).
- `FamiliarConversationService`: the two-transaction Send flow from `architecture.md` §3. The human
  message commits before the provider call. This is not an optimisation; do not merge the
  transactions.
- Every `FamiliarReasoningStatus` maps to a `System` message with the exact `FamiliarFailureWording`
  copy. Add the wording test in the shape of `DecisionOutcomeWordingTests`.
- `tests/FindFamiliar.FakeReasoningProvider`: a scripted provider returning any outcome on demand,
  including a `Detail` containing a fake credential and a fake machine path, so the redaction
  assertion has something real to catch — the same trick `ProviderCapacityServiceTests` uses.
- Timeout is enforced by the caller with a `CancellationTokenSource`, not left to the SDK default.

### Slice 5 — Actions

- `ProposedActionValidator`: the trust table in `architecture.md` §5, exactly. A rejected draft
  produces no proposal and no user-visible complaint.
- `FamiliarActionService.ConfirmAsync` / `DismissAsync`: `WorkApprovalService`'s shape — conditional
  consume by token first, then effects, one transaction, durable links after. Take the write lock
  immediately; do not upgrade from a read.
- Re-validate **inside** the transaction: proposal pending, token current, project active, project
  ownership of the target task, revision gate for `CreateTask` only, no `Started` session for
  `StartPlanner`.
- Map `DbUpdateException`/`SqliteException` through `IsDatabaseBusy` on every path — including
  acquiring the transaction and rolling it back. `DatabaseBusy` is not a lost race.
- Only `ProposalId` and `ExpectedConcurrencyToken` are bound from the form for Confirm/Dismiss. For
  `CreateTask`, the human's edited title and outcome are also bound and validated; everything else is
  server-side.
- Confirmation appends a `Familiar` message stating exactly what was created, inside the same
  transaction.
- Concurrency tests: two simultaneous confirmations produce one task and one loser with truthful
  wording.

### Slice 6 — Claude provider

New project `src/FindFamiliar.Reasoning.Claude`, referencing the `Anthropic` NuGet package. The server
project must **not** reference it — `Program.cs` in the composition root does.

- `client.Messages.Create` (non-streaming). Model from options, default `claude-opus-5`.
  `MaxTokens = 4096`. `OutputConfig.Effort = Effort.Medium`.
- Adaptive thinking is on by default on this model — leave `Thinking` unset and leave `display` at its
  default `omitted`. Do not read, log or persist thinking blocks.
- Do **not** send `temperature`, `top_p`, `top_k` or `budget_tokens`; they are rejected on this model.
- Reply and the optional single action come back via `OutputConfig.Format` — a `JsonOutputFormat` with
  `additionalProperties: false` and a `required` list. **Declare no tools.**
- Check `response.StopReason == "refusal"` **before** reading `Content`; map to `Declined`.
- `ClaudeReasoningFailureClassifier` maps typed SDK exceptions to statuses, most specific first:
  `AnthropicUnauthorizedException` ⇒ `Unauthenticated`; `AnthropicRateLimitException` ⇒ `RateLimited`;
  `Anthropic5xxException` / `AnthropicIOException` ⇒ `Unavailable`; `OperationCanceledException` from
  our own token ⇒ `TimedOut`; anything else ⇒ `Unavailable`. Never let an exception escape
  `RespondAsync`.
- `Detail` is composed here from the status, never from the exception message.
- API key from the `ANTHROPIC_API_KEY` environment variable only. If it is absent, registration falls
  back to the unconfigured provider and the page says so. Never read a key from configuration files.
- **No test in this repository may make a network call.** This project's tests cover the classifier and
  the schema shape only.
- Write `docs/familiar-guide.md` for operators: what the page does, how to configure a provider, what
  each failure state means, and an explicit statement that the Familiar cannot act without
  confirmation.

## Testing

`tests/FindFamiliar.Server.Tests`, following the existing layout: service tests in `Services/`, page
and endpoint tests in `Http/`, schema guards in `Infrastructure/`. See
[`acceptance-checklist.md`](acceptance-checklist.md) for the full list that must exist and pass.

Write the isolation, no-mutation and provider-failure tests **before** the code they cover. They are
the ones most likely to be quietly weakened later.

## Definition of done

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — all pass, three consecutive full runs.
- `dotnet ef migrations has-pending-model-changes` — clean.
- `git diff --check` — clean.
- No credential, host, or machine-local path anywhere in the diff, including tests, except the
  synthetic fixtures that exist to prove redaction.
- ADR-0012 written from the outline in `architecture.md` §7 once the code exists, and marked Accepted
  only when it describes what shipped rather than what was planned.
- `README.md` roadmap and `docs/sprint-acceptance.md` updated on acceptance, not before.
- The reviewer prompt's questions can all be answered from the code without you in the room.
