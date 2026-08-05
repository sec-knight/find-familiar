# Conversational Familiar v0 — Specification

**Status:** Proposed. Nothing in this document is implemented.

**Sprint:** 11. This is the immediate product priority. The anchor-fragment navigation work previously
drafted for Sprint 11 is deferred as a small future usability task; it is not cancelled and not done.

This is the first human-facing Familiar. Today the application can run work and explain a project
(the Demiplane, ADR-0011), but a person cannot *ask it anything*. Every question — "why is this
blocked", "what did the Planner conclude", "what should I do next" — is answered by reading a page
and doing the reasoning yourself.

Familiar v0 adds one thing: a conversation with a reasoning component that is **grounded in
persisted project state and can propose, but never take, action**.

---

## 1. What this is not

Naming the non-goals first, because every one of them is a plausible next step and none of them is
this sprint:

- Not autonomy. The Familiar never acts without an explicit human confirmation click.
- Not a second orchestration path. Actions execute through `IWorkflowDispatchService`, the same
  boundary the manual pages and conversational approval already use.
- Not the Runner. `IRunnerBridge` and the Claude Code adapter exist to execute *sessions in a
  repository*. Conversational reasoning needs no worktree, no repository, no claim and no lease.
  Reusing the Runner would make every question about a project require an available worker.
- Not voice, avatars, streaming, native mobile, multi-model routing, background polling, connectors,
  or a resource catalogue.

## 2. Route and page

One page, one route:

```
/Familiar/{projectId:guid}
```

`Pages/Familiar.cshtml` + `Pages/Familiar.cshtml.cs`, matching `Pages/Demiplane.cshtml`'s
`@page "/Demiplane/{id:guid}"` convention. Unknown or malformed project id → `NotFound()`.

The page shows, in order:

1. **Project identity** — name, purpose, status, current context revision. Links to
   `/Projects/Details/{id}` and `/Demiplane/{id}`.
2. **Deterministic summary** — a short, LLM-free statement of the project's current state, derived
   from the same snapshot the reasoning provider receives. This is always rendered, including when
   the provider is unavailable.
3. **Conversation** — messages oldest-first, each labelled with author, timestamp and (for Familiar
   messages) the reasoning provider and model that produced it.
4. **Proposed actions** — rendered in their own visually distinct region, never inline in the chat
   bubble, each with Confirm and Dismiss.
5. **Input** — one textarea and a Send button.

No auto-refresh. Unlike the Demiplane there is no in-flight work on this page to poll for, and a
meta refresh would re-render a conversation the user is reading.

## 3. Durable conversation model

### 3.1 Why not reuse `Conversation`

The existing `Conversation` aggregate (ADR-0009) is deliberately **not** an open-ended chat: its own
XML doc says so. It has no `ProjectId`, it owns exactly one `WorkProposal`, it reaches a terminal
`Approved`/`Rejected` status, and `WorkApprovalService` consumes it exactly once. A per-project,
never-terminal, many-proposal chat would have to break all four of those invariants, and the intake
flow's safety properties are built on them.

So: a new aggregate, in its own tables, sharing nothing but the database.

### 3.2 Entities

**`FamiliarConversation`** — one per project.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `ProjectId` | Guid | FK → Projects, **cascade**, **unique index** |
| `CreatedUtc` / `UpdatedUtc` | DateTime | |

One conversation per project is the right unit for v0: the subject of the conversation is the
project, continuity across days is the entire point, and a per-session thread would fragment the
memory this application exists to preserve. Multiple threads per project can be added later by
relaxing the unique index; nothing else in the design assumes one.

**The row is created on the first `POST`**, not on `GET`. Creating it lazily when the page is first
opened would be tidier, but it would make a read mutate the database for the sake of a row nobody has
asked for yet. A project with no conversation renders an empty conversation region. GET stays pure.

**`FamiliarMessage`** — append-only.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `ConversationId` | Guid | FK, cascade |
| `Author` | enum (string) | `Human` \| `Familiar` \| `System` |
| `Sequence` | int | **unique** with `ConversationId` — display order never depends on timestamp ties |
| `Content` | string(8000) | user-visible text only |
| `CreatedUtc` | DateTime | |
| `ProviderName` | string(120)? | e.g. `Claude`; null for Human/System |
| `ProviderModel` | string(120)? | e.g. `claude-opus-5` |
| `LatencyMs` | int? | operational metadata |
| `Delivery` | enum (string) | `Delivered` \| `Degraded` \| `Failed` — see §7 |
| `FailureCode` | string(64)? | a fixed code this codebase writes, never provider text |

**`FamiliarMessage` stores no hidden reasoning.** Thinking blocks, tool chatter, raw provider
payloads and prompts are never persisted, never logged, and never rendered. `ConversationMessage`
already carries this rule in its doc comment; it is repeated here because it is the one rule most
easily lost during implementation.

**`FamiliarEvidence`** — optional structured provenance for a Familiar message.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `MessageId` | Guid | FK, cascade |
| `Kind` | enum (string) | `Task` \| `Session` \| `Handoff` \| `ContextEntry` |
| `ReferenceId` | Guid | must exist in the snapshot that produced the message |
| `Label` | string(200) | server-composed display text |

Evidence rows are **server-generated**, not model-authored prose. The provider may cite an id; the
application accepts it only if that id was present in the snapshot it sent, and composes the label
itself from persisted data. An id the provider invented is dropped, silently and without comment,
because a hallucinated citation is not an event worth reporting to the user.

**`FamiliarActionProposal`**

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `ConversationId`, `ProjectId` | Guid | FK; `ProjectId` denormalised so validation never needs a join |
| `MessageId` | Guid | the Familiar message that proposed it |
| `Kind` | enum (string) | `CreateTask` \| `StartPlanner` |
| `Status` | enum (string) | `Pending` \| `Confirmed` \| `Dismissed` |
| `ConcurrencyToken` | Guid | the fence; rotated on every transition |
| `ObservedContextRevision` | int | project revision when proposed |
| `Title` | string(200)? | `CreateTask` only |
| `RequestedOutcome` | string(4000)? | `CreateTask` only |
| `TargetTaskId` | Guid? | `StartPlanner` only, FK → Tasks, restrict |
| `CreatedUtc` / `UpdatedUtc` / `DecidedUtc?` | DateTime | |
| `CreatedTaskId` / `CreatedSessionId` | Guid? | durable links, unique-filtered, restrict |

Parameters are **typed columns, not JSON**, exactly as `WorkProposal` does it. A JSON blob would
move validation from the schema into a parser, and the parser would be reading model output.

Indexes:

- `IX_FamiliarActionProposals_ConversationId_Pending` — unique, filtered `Status = 'Pending'`.
  **At most one actionable proposal per conversation**, which makes concurrent confirmation trivially
  safe for the same reason `IX_SessionHandoffs_TaskId_Pending` does: contenders can only ever race
  for one row. A Familiar reply may therefore carry at most one proposal.
- `CreatedTaskId` and `CreatedSessionId` — unique, filtered `IS NOT NULL`.

### 3.3 Migration

One migration, `FamiliarConversations`. Four new tables, no column added to and no row touched in any
existing table. Nothing is backfilled: a project with no conversation has no conversation, which is
the truth.

## 4. Project snapshot

`IProjectSnapshotService.GetSnapshotAsync(projectId, ct)` returns a `ProjectSnapshot` record, or null
for an unknown project. It performs no writes and calls no provider.

It is built **on top of `IDemiplaneProjectionService`**, not beside it. The Demiplane already owns
every rule about what a task's state is and why (ADR-0011, "display state is derived once"); a second
interpretation for the Familiar is exactly the drift that ADR forbids. The snapshot adds sessions,
context entries and worker availability, and enforces bounds.

### 4.1 Contents and limits

| Section | Ordering | Cap |
|---|---|---|
| Project | — | name; purpose truncated to 1 000 chars; status; context revision |
| Tasks | Demiplane order (needs-attention, then state rank, then `UpdatedUtc` desc) | **20** — title, display state, reason code, reason text, current role, provider, whether a handoff is pending |
| Sessions | `StartedUtc` desc across the project | **10** — role, status, provider, started, completed |
| Pending handoffs | task order | **10** — task title, proposed role, kind |
| Context entries | `State = Active`, `CreatedUtc` desc | **15** — kind, title, task title, first **500** chars of content |
| Demiplane health | — | count per display state, needs-attention count, `HasActiveWork` |
| Providers | — | each `ProviderCapacitySnapshot`'s status/confidence/detail — currently `Unknown` (ADR-0011) |
| Workers | — | enabled worker **count**, distinct declared roles, availability counts |
| Limitations | — | see below |

**Workers are reported as counts and roles only.** `WorkerKey` and `DisplayName` are
administrator-chosen strings that in practice name machines. They are not sent to a provider.

`ProjectSnapshot.Limitations` is a list of plain statements the snapshot builder *knows* to be true,
composed deterministically — e.g. "Showing 20 of 47 tasks, ordered by attention then recency",
"Provider remaining capacity is unknown; this application cannot read it", "Worker capabilities are
self-reported and are not verified", "Context entries older than the 15 shown are not included".
This section is what lets the behaviour contract in §6 say *"say what you do not know"* and have it
mean something specific.

### 4.2 Size

`ProjectSnapshot.EstimatedCharacters` is the **deterministic serialized-size estimate used for
snapshot reduction**, measured with the one canonical serializer in
`ProjectSnapshotSerialization`. Two thresholds:

- `MaxSnapshotCharacters = 24_000` — the budget.
- If over budget, drop sections in a fixed order and record the drop in `Limitations`:
  context entries → sessions → tasks beyond the first 5.
- If still over budget after that floor, the snapshot is returned with
  `IsWithinBudget = false`. The page then renders the deterministic summary and refuses the provider
  call with an honest message (§7). It does not silently send a truncated project.

Characters, not tokens, because computing tokens means calling a provider and the snapshot builder
must be callable — and testable — with no provider configured at all.

**It is an estimate, not a byte-for-byte length.** `EstimatedCharacters`, `IsWithinBudget` and
`ObservedAt` are held at deterministic placeholders while measuring: the first two would otherwise
depend on their own result, and a serialized instant varies in width with the clock, so a
clock-dependent budget would drop a section on one page load and keep it on the next. A fully
populated snapshot may therefore serialize a small, bounded number of characters longer than
`EstimatedCharacters`, and `IsWithinBudget` may differ by one character at the boundary. The
determinism is the property worth keeping; the last character is not.

The supported invariant:

> `EstimatedCharacters` is the deterministic serialized-size estimate used for snapshot reduction.
> The final provider envelope must be serialized and checked again immediately before transmission.

## 5. Reasoning provider

```csharp
public interface IFamiliarReasoningProvider
{
    string Provider { get; }

    Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FamiliarReasoningRequest(
    ProjectSnapshot Snapshot,
    IReadOnlyList<FamiliarTurn> History,   // user-visible text only, bounded
    string UserMessage,
    string BehaviorContract);

public enum FamiliarReasoningStatus
{
    Answered, Unavailable, Unauthenticated, TimedOut,
    RateLimited, Malformed, Declined
}

public sealed record FamiliarReasoningOutcome(
    FamiliarReasoningStatus Status,
    string? Reply,
    IReadOnlyList<ProposedActionDraft> Actions,
    FamiliarProviderMetadata Metadata,
    string? Detail);
```

Rules the interface exists to enforce:

- **It never throws.** Every failure is a typed status with a safe `Detail`, exactly as
  `IProviderCapacityReader` returns `ProviderCapacitySnapshot.Faulted` rather than breaking a page.
- **It never invents.** `Answered` requires a non-empty `Reply`. Anything else carries `Reply = null`
  and the page composes the wording.
- **`Detail` is safe to display.** No stack traces, no file paths, no URLs, no header values, no
  fragment of a credential. `ProviderCapacityServiceTests` already asserts this property for capacity
  readers; the same assertion applies here.
- **It is provider-neutral.** Nothing in the request or the outcome names Claude. Swapping in Hermes,
  OpenAI or a local model is a DI registration change.

`History` is bounded by the caller: the last **10** messages, `Content` as stored (already ≤ 8 000
chars). System messages are excluded — they are page-composed error notes and re-feeding them teaches
the model to imitate error text.

### 5.1 First implementation

`ClaudeFamiliarReasoningProvider`, using the official `Anthropic` NuGet SDK against the Messages API,
non-streaming (`client.Messages.Create`). Model `claude-opus-5`, `MaxTokens = 4096`,
`OutputConfig.Effort = Medium`, adaptive thinking left at its default with `display` omitted — the
raw chain of thought is never returned and nothing about it is stored.

The reply and any action proposal come back as **structured output** (`OutputConfig.Format`, a JSON
schema with `reply` and an optional single `action`). **No tools are declared.** A model with no
tools cannot request one, which is a stronger guarantee than a harness that declines them.

`stop_reason == "refusal"` maps to `Declined`, checked before reading `Content`. Sampling parameters
and `budget_tokens` are not sent (they are rejected on this model).

**The default registration is `UnconfiguredFamiliarReasoningProvider`**, which returns `Unavailable`
with the reason "No reasoning provider is configured." The Claude implementation is registered only
when `Familiar:Reasoning:Provider` says so. This mirrors `UnknownProviderCapacityReader`: the honest
default is stated, not simulated, and the application runs with no credentials at all.

Configuration lives under `Familiar:Reasoning:` — `Provider`, `Model`, `TimeoutSeconds` (default 60),
`MaxOutputTokens`, `Effort`. **The API key is read from the `ANTHROPIC_API_KEY` environment variable
only.** It is never in `appsettings*.json`, never in the database, never logged.

## 6. Familiar behaviour contract

The contract ships as `FamiliarBehaviorContract.Text` — a `const string` in the server project, sent
as the system prompt and asserted by tests. **Code is the single copy.** This document records its
intent, not a second copy that would drift and win the argument in the room while losing it in the
repository.

Its intent, which the reviewer checks the text against:

- Warm, direct, concise. Answers the question asked.
- Answers **only** from the snapshot and the conversation. Anything not in the snapshot is unknown.
- Distinguishes three registers explicitly: *this is recorded*, *this is what I infer from it*, and
  *I cannot tell*. It never presents an inference as a record.
- Never claims an action happened. It may say what it proposes; persisted state is the only thing
  that says what occurred, and the page renders that separately.
- Repeats the snapshot's `Limitations` when they bear on the answer, rather than filling the gap.
- Recommends at most **three** next steps, and only when asked or clearly useful.
- Separates chat from commands: a question is answered in prose; a request to change state produces
  at most one proposed action, described plainly, and nothing else.
- Emits no URLs, no shell commands, no file paths, and no instructions addressed to the software.
- When the user asks for something outside `CreateTask` / `StartPlanner`, says plainly that it cannot
  do that yet and names the page where a human can.

## 7. Failure states

Every one of these renders the page fully, with the deterministic summary intact. The conversation is
usable as a project read-out with no provider at all.

| Condition | Persisted | Shown |
|---|---|---|
| No provider configured | Human message; `System` message | "No reasoning provider is configured, so I can only show you what is recorded." |
| Provider unreachable | Human message; `System` message, `Delivery = Failed`, `FailureCode = provider-unavailable` | "The reasoning provider could not be reached. Nothing was changed." |
| Authentication missing / rejected | as above, `provider-unauthenticated` | "The reasoning provider rejected this application's credentials. This is a server configuration problem, not something you did." |
| Timeout | as above, `provider-timeout` | "The reasoning provider did not answer within N seconds. Your message was saved; try again." |
| Rate limited | as above, `provider-rate-limited` | "The reasoning provider is rate limiting this application right now. Try again shortly." |
| Malformed response | as above, `provider-response-unusable` | "The reasoning provider returned a response this application could not use. Nothing was changed." |
| Provider declined | as above, `provider-declined` | "The reasoning provider declined to answer this message." |
| Snapshot over budget | Human message; `System` message, `snapshot-too-large` | "This project is too large to summarise for the reasoning provider safely, so I have not sent it. The summary above is complete and accurate." |
| Proposal stale | — | The Confirm button is replaced by an explanation of what changed and a link to refresh. |
| Action no longer valid at execution | proposal untouched | The specific reason, from a typed outcome — task already running, project archived, revision moved, already decided. |
| Server knows less than the user expects | — | The `Limitations` block is always rendered next to the summary, not hidden behind a disclosure. |

The wording table is the deliverable: `DecisionOutcomeWordingTests` already establishes that this
codebase asserts its own copy. Familiar v0 does the same.

**Nothing above may say a race was lost unless a competing decision actually exists.** SQLite busy and
locked conditions classify as `DatabaseBusy` on every path, including transaction acquisition and
rollback, via the existing `SessionHandoffApprovalService.IsDatabaseBusy`.

## 8. Actions

Two, and only two.

### `CreateTask`

- **What will happen:** one new task in this project, status `Ready`. No session starts. No worker is
  notified.
- **Why:** the sentence from the Familiar's reply that motivated it, as the human read it.
- **Parameters:** title, requested outcome — both editable by the human before confirming.
- **State change:** one `Tasks` row; the project's context revision increments once.
- **Executes via:** `IWorkflowDispatchService.CreateReadyTask`.
- **Gates:** proposal `Pending`; token matches; project `Active`; `ObservedContextRevision` equals the
  project's current revision. The revision gate applies here because the human is approving *content*
  they reviewed, which is exactly the case ADR-0009's gate protects.

### `StartPlanner`

- **What will happen:** one `Started` Planner session on an existing task in this project. An eligible
  worker may claim it automatically (ADR-0008).
- **Parameters:** the target task — chosen from this project's tasks, never a free-text id.
- **State change:** one `AgentSessions` row; the project's context revision increments once; the
  session records the revision it read.
- **Executes via:** `IWorkflowDispatchService.StartSessionForTaskAsync(taskId, Planner, null, null, now)`.
  Provider and external session reference stay null — the Familiar never chooses a worker.
- **Gates:** proposal `Pending`; token matches; project `Active`; **target task belongs to this
  project**; no `Started` session on the task (enforced ultimately by
  `IX_AgentSessions_TaskId_Started`). **No revision gate** — the decision is "run this role now", and
  the session reads whatever context is current at its own start, exactly the reasoning ADR-0010
  recorded for handoffs.

Both confirmations follow the Sprint 08/09 shape: conditional consume of the `Pending` row by token
first, then the effects, all inside one transaction; the durable link written after the rows exist.

Excluded from v0, deliberately: handoff approve/decline (already has a home on the Demiplane, and
duplicating it would create a second approval path for the same row), worker restart, connector
repair, Git operations, arbitrary commands. None has a service boundary safe to drive from chat.

## 9. Security and privacy

- **Antiforgery** on every write. All writes are Razor Page `OnPost*` handlers; there is no minimal
  API endpoint for chat. `AntiforgeryHttpClient` covers this in tests today.
- **No secrets anywhere near the model.** The key is environment-only. The snapshot builder reads
  four tables and never `appsettings`. Worker keys and display names are excluded (§4.1).
  Repository paths are already absent from the server by ADR-0008's design.
- **Bounded input.** User message ≤ 4 000 chars; history ≤ 10 messages; snapshot ≤ 24 000 chars, hard
  refusal above it.
- **No tool surface.** The provider is called with no tools declared, so there is no execution channel
  to abuse.
- **Model output is inert data.** Rendered with Razor's default encoding — no `Html.Raw`, no
  markdown-to-HTML, no auto-linking. Line breaks come from CSS `white-space: pre-wrap`. **A URL the
  model writes is never a link, and a command it writes is never a button.**
- **Project isolation.** Every snapshot query filters on `projectId`, as `DemiplaneProjectionService`
  does. Proposals carry `ProjectId` and validation re-checks it at execution time; a proposal id from
  another project cannot be confirmed from this page.
- **Validation at execution time, not at proposal time.** The proposal is a record of what a human was
  shown. Every gate is re-evaluated inside the confirming transaction.
- **Failure text is authored here.** Provider exception messages never reach the page. Only the fixed
  codes in §7 do.

## 10. Acceptance

Familiar v0 is accepted when a user can open `/Familiar/{projectId}`, read an accurate deterministic
summary of a project with no provider configured, hold a conversation grounded in that project once a
provider is configured, confirm a proposed task or Planner session, and see that the resulting work is
indistinguishable — to the queue, the runner and the Demiplane — from work started by hand.

See [`acceptance-checklist.md`](acceptance-checklist.md) for the itemised bar.
