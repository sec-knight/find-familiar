# ADR-0012: Conversational Reasoning and Human-Confirmed Action

**Status:** Accepted

**Date:** 2026-08-06

---

## Context

Through Sprint 10 this application had exactly one kind of intelligence: a coding worker executing a
session against a repository, brokered by the provider-neutral Runner (ADR-0006) and the Claude Code
adapter (ADR-0007). Sprint 10 gave the project a voice (ADR-0011) but not a listener — the Demiplane
states facts, and a human does the reasoning.

Adding a conversational Familiar raises a question the existing ADRs do not answer: **is a chat turn
a kind of session?** The answer shapes the schema, the concurrency model and the failure surface, so
it is recorded here.

It also raises a sharper one. A reasoning provider produces text. Text is persuasive, arrives in the
shape of an answer, and is wrong sometimes in ways that are hard to see. Everything below exists to
keep that text from becoming authority.

## Decision

### 1. Four concerns, named and separated

- **Deterministic project state** — persisted rows and the Demiplane projection over them. The only
  authority on what is true. No model reads or writes it directly.
- **Coding workers** — repository-scoped session execution, claimed, leased and fenced (ADR-0005,
  0006, 0008). Unchanged by this decision.
- **Conversational reasoning** — a request-scoped, tool-less, provider-neutral call that receives a
  bounded snapshot and returns text. Holds no claim, needs no worker, touches no repository.
- **Human-confirmed action** — the only bridge from reasoning back to state, running through the
  application services that already own those mutations.

### 2. Conversational reasoning is not a session

It gets no `AgentSession` row, no role, no claim, no lease, and no place in the `Started`-uniqueness
invariant.

Sessions are execution authority with recovery semantics built for a worker that might crash
mid-task. A web request that times out needs none of that, and giving a chat turn a session row would
let a conversation occupy a task's `Started` slot — a question about a task would block work on it.

### 3. Conversational reasoning does not use the Runner

Two provider abstractions coexist because they answer different questions: *"execute this assignment
in a repository"* and *"answer this question about this project"*. Collapsing them would make every
question depend on worker availability — the exact `NoWorkerForRole` trap ADR-0011 surfaces as a
*blocked* state.

### 4. Model output is data, never authority

It cannot name a table, call a tool, execute a command, or produce a clickable link. This is
structural rather than instructed:

- **No tools are declared on any provider call.** A model with no tools in its schema cannot request
  one — a stronger guarantee than a harness that declines them.
- **Rendering is inert.** Stored text is HTML-encoded with no markdown rendering and no autolinking,
  so a URL a model writes is characters and a command it writes is characters.
- **Proposals are validated twice** — once against the snapshot that produced them, and again inside
  the transaction that would execute them.
- **The action surface is a closed two-member enum** with no catch-all value, so an unparseable kind
  has nowhere to be stored.

### 5. No hidden reasoning is persisted

Only user-visible text, structured evidence references, proposals and operational metadata. The
schema has no column for a prompt, a thinking block, a raw payload or a provider exception, so
nothing can write one — the guarantee is the absence of the column, not a rule about using it.

Provider failure text is never persisted, logged or rendered. The page composes what a person reads
from a fixed wording table this application owns.

### 6. The honest default is "unavailable"

Shipping with no provider configured yields a page that still works and says why the conversation
does not — the same stance ADR-0011 took on provider capacity. No credential is required to run this
application.

### 7. Provider portability is a first-class property

The shipped implementation targets any endpoint speaking the OpenAI chat-completions shape, which
covers local runtimes (llama.cpp, vLLM, LM Studio, Ollama) and hosted services (OpenAI, Groq,
Together, DeepInfra, OpenRouter) alike. Choosing between them is a base address in configuration.

An API key is read from an environment variable whose *name* is configured; no property in the
options type can hold a key, so one cannot be committed or printed by a configuration dump.

## What was rejected

**Reusing the ADR-0009 `Conversation` aggregate.** It is a work-intake interaction with exactly one
proposal and a terminal status, and `WorkApprovalService`'s safety rests on all three properties. A
per-project, never-terminal, many-proposal chat would have to break each of them. A new aggregate in
its own tables was cheaper than weakening a shipped safety boundary.

**One transaction for the send flow.** The human message commits *before* any provider I/O, in its own
transaction. A single transaction spanning a third-party network call would hold a SQLite write lock
for its duration — on a database the runner, the capture path and the claim scan all write to — and a
provider that hung would take the user's words with it.

**A native Anthropic SDK provider in its own project.** Planned, written, and dropped before merge.
It required either a shared-abstractions project (which cascades, since `ProjectSnapshot` reaches
most of the domain) or moving `Program.cs` into a new host project (which touches the test factory
600+ tests depend on). Since an OpenAI-compatible proxy reaches Claude through the shipped provider,
a second implementation bought first-party prompt caching at the cost of a structural change. It can
be revisited when that caching matters.

**Trusting structured output.** Endpoints advertise `response_format` support and then ignore it —
observed returning prose against a schema-constrained request. Replies are therefore parsed and
validated regardless, and an unusable one is reported rather than guessed at.

## Consequences

- **Two provider abstractions to maintain.** Accepted: they have genuinely different lifetimes,
  failure modes and trust levels.
- **A second conversation aggregate.** Accepted, for the reason above.
- **Answers are only as good as the snapshot bounds.** Accepted and made visible: the bounds are
  published to the user beside every answer rather than hidden.
- **Two actions is a small surface.** Accepted for now. Note that neither records a decision, so the
  Familiar cannot yet write down what a conversation concluded — the gap most relevant to this
  project's stated purpose, and the strongest candidate for a third kind.
- **A local model needs AVX2.** Measured on a pre-AVX host: 3.6 tokens/sec prompt processing, roughly
  half an hour per question. The provider is unchanged; the hardware requirement is real and
  documented.

## Verification

- `IWorkflowDispatchService` is referenced from exactly one file under `Services/Familiar` —
  `FamiliarActionService`'s constructor. There is no other path from a reply to persisted work.
- Two concurrent confirmations produce exactly one task and exactly one session, proven on real
  file-backed SQLite with independent connections.
- SQLite busy and locked classify as `DatabaseBusy` on every path including transaction acquisition,
  rollback and the post-race read, and are never reported as a lost race.
- A synthetic credential and machine path planted in a provider error body and in a thrown exception
  reach neither the page nor the database.
- No test in this repository opens a network connection.
