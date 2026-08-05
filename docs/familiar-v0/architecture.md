# Conversational Familiar v0 — Architecture

Companion to [`specification.md`](specification.md). That document says what v0 must do; this one says
how the pieces fit and why they are separated the way they are.

---

## 1. Three layers, and the line between them

```
        ┌──────────────────────────────────────────────────────────┐
        │  /Familiar/{projectId}   (Razor Page — renders, derives  │
        │                           nothing, decides nothing)      │
        └────────────┬─────────────────────────┬───────────────────┘
                     │                         │
   ┌─────────────────▼──────────────┐   ┌──────▼────────────────────────┐
   │ 1. DETERMINISTIC STATE         │   │ 3. PROPOSED ACTIONS           │
   │                                │   │                               │
   │ IProjectSnapshotService        │   │ IFamiliarActionService         │
   │   └─ IDemiplaneProjectionSvc   │   │   └─ IWorkflowDispatchService  │
   │   └─ FamiliarDbContext         │   │        (shared, unchanged)     │
   │   └─ IProviderCapacityService  │   │                               │
   │                                │   │ Human confirmation required.  │
   │ No LLM. No writes.             │   │ Every gate re-checked here.   │
   └─────────────────┬──────────────┘   └───────────────────────────────┘
                     │  bounded snapshot                    ▲
   ┌─────────────────▼──────────────────────────────┐       │ proposal drafts
   │ 2. REASONING                                    │       │ (validated, never
   │ IFamiliarReasoningProvider                      │───────┘  executed)
   │   ClaudeFamiliarReasoningProvider   (default:   │
   │   UnconfiguredFamiliarReasoningProvider)        │
   │                                                 │
   │ Sees: snapshot + visible history + user message │
   │ Sees not: database, tools, filesystem, secrets  │
   └─────────────────────────────────────────────────┘
```

The load-bearing property is the direction of the arrows. Layer 2 receives a value object and returns
a value object. It has no `DbContext`, no `IWorkflowDispatchService`, no `HttpContext`, and no tools.
**There is no code path by which a provider response reaches the database without passing through a
human confirmation and a re-validated application service.** That is not a policy the prompt enforces;
it is a shape the type graph enforces.

## 2. Why this is not the Runner

`FindFamiliar.Runner` and `FindFamiliar.Adapter.Claude` exist to execute a *session against a
repository*: a claim, a lease, a fencing token, a worktree, a captured result. Conversational
reasoning has none of those needs and one incompatible requirement — it must answer in seconds, on
the web request, whether or not any worker is online.

Routing chat through the Runner would mean:

- a question about a project blocks on worker availability (the exact `NoWorkerForRole` trap ADR-0011
  surfaces as a *blocked* state);
- a chat turn would occupy the task-level `Started` uniqueness slot, or need a new session role;
- the adapter's repository-path policy and worktree inspector would run for a request that touches no
  repository.

So `IFamiliarReasoningProvider` is a second, unrelated provider abstraction. The two never call each
other. `IProviderCapacityReader` stays where it is and keeps reporting `Unknown` (ADR-0011); the
Familiar page shows that reading rather than claiming to know its own budget.

## 3. Request flow

### `GET /Familiar/{projectId}`

```
Page → IProjectSnapshotService.GetSnapshotAsync(projectId)   ── null ⇒ 404
     → IFamiliarConversationService.GetAsync(projectId)      ── may be null (no conversation yet)
     → render: identity, deterministic summary, limitations, messages, pending proposal, input
```

No writes. No provider call. A `GET` never creates the conversation row — a project you only look at
stays untouched.

### `POST` handler `Send`

```
1. Validate: non-empty, ≤ 4 000 chars, project exists and is loadable.
2. Transaction A:
     get-or-create FamiliarConversation for this project
     append Human message at NextSequence
     commit                                        ← the user's words are durable before any I/O
3. Build snapshot.  Over budget ⇒ append System message (snapshot-too-large), redirect. Done.
4. Call IFamiliarReasoningProvider with (snapshot, last 10 visible messages, user message, contract).
   Bounded by Familiar:Reasoning:TimeoutSeconds.
5. Transaction B, by outcome status:
     Answered ⇒ append Familiar message (+ evidence rows for ids present in the snapshot)
                (+ at most one Pending proposal, if the draft validates)
     anything else ⇒ append System message with the fixed FailureCode
     commit
6. PRG: RedirectToPage.
```

Two transactions, not one, and deliberately. The user's message must survive a provider that hangs,
crashes the request, or is killed by a deploy. A single transaction spanning a network call to a third
party would hold a SQLite write lock for the duration — on a database the runner, the capture path and
the claim scan are all writing to.

### `POST` handler `Confirm` / `Dismiss`

```
Confirm: IFamiliarActionService.ConfirmAsync(proposalId, expectedToken)
  ├ conditional consume:  UPDATE … WHERE Id = @id AND Status = 'Pending'
  │                         AND ConcurrencyToken = @token AND CreatedTaskId IS NULL
  │                       SET Status = 'Confirmed', ConcurrencyToken = new, UpdatedUtc = now
  ├ affected ≠ 1 ⇒ rollback, re-read committed state, report the truth
  ├ re-validate every gate inside the transaction (project active, task ownership, revision, …)
  ├ effects via IWorkflowDispatchService
  ├ append a Familiar message stating exactly what was created
  └ write CreatedTaskId / CreatedSessionId, commit
```

This is the `WorkApprovalService` shape, unchanged, for the same two reasons it was chosen there:
the database picks the winner, and the winner's complete effects commit or do not.

Only the proposal id and the token the rendered page carried are model-bound from the form. Kind,
parameters, project and task are read server-side from the row, so a crafted post cannot choose an
action or retarget one.

## 4. New types

```
src/FindFamiliar.Server/
  Domain/
    FamiliarConversation.cs
    FamiliarMessage.cs            FamiliarMessageAuthor.cs   FamiliarMessageDelivery.cs
    FamiliarEvidence.cs           FamiliarEvidenceKind.cs
    FamiliarActionProposal.cs     FamiliarActionKind.cs      FamiliarActionStatus.cs
  Services/Familiar/
    ProjectSnapshot.cs                    — the record + its sections + limits as consts
    IProjectSnapshotService.cs / ProjectSnapshotService.cs
    IFamiliarConversationService.cs / FamiliarConversationService.cs
    IFamiliarActionService.cs / FamiliarActionService.cs
    FamiliarBehaviorContract.cs           — the const system prompt, the only copy
    FamiliarSummaryWriter.cs              — deterministic, LLM-free project summary
    FamiliarFailureWording.cs             — the §7 wording table, public and asserted
    Reasoning/
      IFamiliarReasoningProvider.cs       — request/outcome records, status enum
      UnconfiguredFamiliarReasoningProvider.cs
      FamiliarReasoningOptions.cs
      ProposedActionValidator.cs          — draft ⇒ proposal, or nothing
  Pages/
    Familiar.cshtml / Familiar.cshtml.cs

src/FindFamiliar.Reasoning.Claude/        — new project
  ClaudeFamiliarReasoningProvider.cs
  ClaudeReasoningSchema.cs                — the JSON schema for structured output
  ClaudeReasoningFailureClassifier.cs     — exception/stop-reason ⇒ FamiliarReasoningStatus

tests/FindFamiliar.FakeReasoningProvider/ — scripted outcomes, mirrors FindFamiliar.FakeAdapter
```

The Claude implementation lives in its own project so the server never references the `Anthropic`
package, and so a test run cannot accidentally reach the network. Server holds the abstraction; the
composition root in `Program.cs` binds one implementation. `FindFamiliar.Server.Tests` references the
fake, exactly as it references `FindFamiliar.FakeAdapter` today.

## 5. Where the model's output is trusted, and where it is not

| Provider output | Treatment |
|---|---|
| `Reply` text | Stored and rendered as **encoded text**. Never HTML, never a link, never markdown. |
| Evidence ids | Accepted only if present in the snapshot just sent. Labels composed server-side from persisted rows. Unknown ids dropped. |
| Action kind | Must parse to `CreateTask` or `StartPlanner`. Anything else ⇒ no proposal, reply still shown. |
| `CreateTask` title / outcome | Length-validated, trimmed, **editable by the human** before confirming. Empty ⇒ no proposal. |
| `StartPlanner` target task | Must be a task id **present in the snapshot**, i.e. in this project. Anything else ⇒ no proposal. |
| Anything else in the payload | Ignored. |

A rejected draft is not an error. The reply is still shown; the user simply gets no button. Reporting
"the model proposed something invalid" would teach users to read model intent as system state.

## 6. Deterministic summary

`FamiliarSummaryWriter.Compose(snapshot)` produces a handful of plain sentences from the snapshot:
task counts by display state, what needs attention and why, what is running, what is blocked, and the
project's `Limitations`. It reuses `FamiliarSummaryComposer`'s discipline — every sentence must be
supported by a field that is non-null, and there is no sentence for a field that is null.

It exists for three reasons: it is the page's floor when the provider is unavailable, it is what the
reasoning layer is measured against, and it is the thing a reviewer can diff against the database by
hand.

## 7. Proposed ADR

**ADR-0012: Conversational Reasoning, Coding Workers, and Human-Confirmed Action**

*Status:* Proposed. The outline below is the plan of record; the ADR file itself is not written until
the implementation exists, and it is marked Accepted only once the shipped code matches it. This
repository does not accept an ADR for work that has not been done.

**Context.** Through Sprint 10 the application has exactly one kind of intelligence: a coding worker
executing a session against a repository, brokered by the provider-neutral Runner (ADR-0006) and the
Claude Code adapter (ADR-0007). Sprint 10 gave the project a voice (ADR-0011) but not a listener: the
Demiplane states facts, and a human does the reasoning. Adding a conversational Familiar raises a
question the existing ADRs do not answer — is a chat turn a kind of session? The answer shapes the
schema, the concurrency model and the failure surface, so it is recorded before the code exists.

**Decision.**

1. **Four concerns, named and separated.**
   - *Deterministic project state* — persisted rows and the Demiplane projection over them. The only
     authority on what is true. No model reads or writes it directly.
   - *Coding workers* — repository-scoped session execution, claimed, leased and fenced (ADR-0005,
     0006, 0008). Unchanged by this decision.
   - *Conversational reasoning* — a request-scoped, tool-less, provider-neutral call that receives a
     bounded snapshot and returns text. Holds no claim, needs no worker, touches no repository.
   - *Human-confirmed action* — the only bridge from reasoning back to state, and it runs through the
     application services that already own those mutations.

2. **Conversational reasoning is not a session.** It gets no `AgentSession` row, no role, no claim, no
   lease and no place in the `Started`-uniqueness invariant. Rationale: sessions are execution
   authority with recovery semantics built for a crashing worker; a web request that times out needs
   none of that, and giving a chat turn a session row would let a conversation block a task.

3. **Conversational reasoning does not use the Runner abstraction.** Two provider abstractions coexist
   because they answer different questions — "execute this assignment in a repository" and "answer
   this question about this project". Collapsing them would make every question depend on worker
   availability.

4. **Model output is data, never authority.** It cannot name a table, call a tool, execute a command,
   or produce a clickable URL. Structured proposals are validated against the snapshot that produced
   them, and validated again at execution time.

5. **No hidden reasoning is persisted.** Only user-visible text, structured evidence references,
   proposals, and operational metadata.

6. **The honest default is "unavailable".** Shipping with no provider configured yields a page that
   still works, and says why the conversation does not — the same stance ADR-0011 took on provider
   capacity.

**Consequences.**

- Two provider abstractions to maintain. Accepted: they have genuinely different lifetimes, failure
  modes and trust levels.
- A second conversation aggregate alongside ADR-0009's intake `Conversation`. Accepted, because the
  intake aggregate's safety rests on invariants an open chat would have to break; the alternative is
  weakening a shipped safety boundary to save a table.
- Familiar answers are only as good as the snapshot bounds. Accepted and made visible: the bounds are
  published to the user in every reply's context, not hidden.
- Two actions is a small surface. Accepted: every other candidate either lacks a safe service boundary
  or already has a human-facing home.

## 8. Implementation slices

Each slice builds, passes the full suite, and is independently reviewable.

| # | Slice | Contents | Rough size |
|---|---|---|---|
| 1 | **Snapshot** | `ProjectSnapshot`, `IProjectSnapshotService`, limits, `Limitations`, `FamiliarSummaryWriter`. No schema change, no page, no provider. | ~1 day |
| 2 | **Schema** | Four entities, `FamiliarDbContext` configuration, indexes, one migration, migration test. | ~0.5 day |
| 3 | **Page, read-only** | `/Familiar/{projectId}`, identity, deterministic summary, limitations, empty conversation, links. | ~0.5 day |
| 4 | **Conversation + provider abstraction** | `IFamiliarReasoningProvider`, unconfigured default, behaviour contract const, `FamiliarConversationService`, Send handler, all §7 failure wording, fake provider project. Still no real provider. | ~1.5 days |
| 5 | **Actions** | `FamiliarActionProposal` flow, validator, `FamiliarActionService`, Confirm/Dismiss UI, staleness display, concurrency tests. | ~1.5 days |
| 6 | **Claude provider** | `FindFamiliar.Reasoning.Claude`, structured output schema, failure classifier, DI wiring, options, operator guide. First and only slice that can talk to a network. | ~1 day |

Slices 1–5 are fully testable with no credentials and no network. That ordering is the point: the
provider is the last thing added and the first thing that can be swapped.

## 9. Deferred, with reasons

| Deferred | Why |
|---|---|
| Streaming replies | Needs a second transport and a partial-persistence story. Non-streaming answers in seconds at this snapshot size. |
| Multiple conversations per project | Relax one unique index when a real need appears. Nothing else assumes one. |
| Server-side refusal fallbacks (`fallbacks`) | Adds a second model and beta surface for a failure the page already handles honestly. |
| Prompt caching | Worth it once the contract and snapshot shape settle; premature while both are changing. Note the snapshot is volatile and belongs *after* any future breakpoint. |
| Token-accurate budgeting | Requires a provider call to count. Characters are honest and testable offline; revisit with real usage data. |
| Approve/decline handoffs from chat | Already on the Demiplane. A second approval path for one row is how two approvals happen. |
