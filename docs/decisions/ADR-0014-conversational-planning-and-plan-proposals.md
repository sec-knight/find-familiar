# ADR-0014: The conversation is the control surface

- Status: Proposed
- Date: 2026-08-06
- Supersedes: none
- Related: ADR-0003 (session result capture), ADR-0004 (assignment packets),
  ADR-0010 (human-gated handoff), ADR-0012 (human-confirmed action),
  ADR-0013 (talk lane / do lane split)

## Context

Sprint 12 delivered a Familiar that holds a grounded conversation and changes nothing. Asked on its
first day to close a task that was demonstrably finished, it refused correctly and told the operator
to go do it by hand.

That refusal exposed a design assumption worth stating and rejecting: **that the conversation advises
and the task pages act.** An earlier draft of this ADR encoded it, routing plan approval and session
starts through the Tasks page. That is backwards for what this system is meant to be.

The Familiar is not a reporting layer over a task tracker. It is meant to hold the project's context
and run the work: decide what should happen, propose it, and once a human agrees, create the tasks,
start the Planner, Implementer and Reviewer sessions, give each the context it needs, and fold the
results back into what it knows.

Most of that machinery already exists and predates the Familiar:

- sessions already receive assembled context (ADR-0004);
- session results are already captured atomically as context entries (ADR-0003);
- role handoffs are already human-gated (ADR-0010);
- proposals already render inline in a conversation and are confirmed there (ADR-0012);
- execution is already provider-neutral (ADR-0006, ADR-0007).

Three things are missing, and only one of them is large.

## Decision

### 1. Every human gate is answerable in the conversation

Plan approval, handoff approval, result acceptance, unblocking. All of it surfaces in the conversation
and is decided there, using the pattern ADR-0012 already proved: the proposal renders inline in the
transcript, the human confirms or declines in place, and the decision carries the concurrency token
the rendered page showed.

The task pages keep their controls. This is not a migration away from them — two doors to the same
decision is fine, and the pages remain the audit surface. What changes is that the conversation is the
door that always works, so a person never has to leave it to make the system move.

### 2. Retrieval is server-side and deterministic, not a model-driven tool call

The Familiar cannot currently read context entries. The standing brief carries projects and tasks
only, which means the results harvested since Sprint 9 — and the Decision records describing why this
system is shaped the way it is — are invisible to it. The loop this project exists to close has been
open the whole time.

The Sprint 12 plan sketched `search_context(query, projectScope?)` as a tool the model calls. This ADR
chooses the simpler shape first: **the server searches, from the person's message and the
conversation's focus, and merges what it finds into the pack before the model is called.**

- ADR-0013 recorded that the chosen model's tool-calling reliability is unproven. A tool that silently
  fails to fire produces an answer drafted blind by something that does not know it is blind.
  Deterministic retrieval cannot fail to fire.
- Search is a read. Model-driven tools earn their complexity when the model must decide *whether* to
  act; here it decides only what to search for, and the person's own message is already a good query.
- One round trip per turn keeps the lane's latency budget, which is the reason the lane exists.

The cost is that the model cannot follow a thread — one retrieval per turn, no refining. Acceptable
while the corpus is small, and the upgrade path stays open: either way the result lands in the pack,
and the pack is what gets cited.

Retrieval that finds nothing enters the prompt as an explicit statement of that fact.

### 3. A plan is one proposal carrying many items, approved as a unit

```
FamiliarPlanProposal            (at most one undecided per conversation)
  conversation, project, status, concurrency token
  observed context revision
  drafted from: entity_refs[]
  items[]:
    title, requested outcome
    role to start (or none)
    evidence refs[]
    included (human, default true)
```

One row, reusing the shape of `IX_FamiliarActionProposals_ConversationId_Pending`: contenders race for
one row, a human decides once, and a half-approved sprint cannot exist in the database.

The items are not tasks. They are a record of what a person will be shown — not authority to act.

### 4. Approval creates the work *and* starts the sessions the plan named

Approving a plan does what the plan said it would do, including starting sessions. Requiring a second
trip elsewhere to start each one would rebuild exactly the split this ADR rejects.

The blast radius is real and is handled by disclosure and itemisation rather than by an extra door:

- the card states plainly what will happen — how many tasks, which sessions start immediately — before
  anything is clicked;
- each item can be excluded, and each title and outcome edited, with the human's version being what
  gets created;
- the applying transaction re-checks project status, observed context revision, and every per-item
  gate, refusing with the specific reason if the world moved;
- the existing single-started-session-per-task invariant still bounds what can run at once.

### 5. The invariant is unchanged

**The talk lane never writes to project state.** It produces prose and proposals; proposals are
durable records; a human approves in the conversation; execution runs through `FamiliarActionService`
with gates re-checked inside the executing transaction.

The talk lane holds no reference to `IWorkflowDispatchService`. What changes in Sprint 13 is *where a
human stands when they decide*, not how many code paths can create work. There is still exactly one.

## Consequences

### Positive

- The conversation becomes sufficient. A person can run a development cycle without opening a task
  page, which is the thing this project has been building towards since the roadmap said "first
  Familiar-managed development cycle".
- The harvest loop finally closes: a session's result becomes a context entry and the Familiar can
  read it on the next turn.
- Retrieval has no dependency on provider tool-calling reliability.
- Nothing about execution changes. The Runner, the adapter and the worker are untouched.

### Negative

- A plan is persuasive in a way a single action is not, and approving one now starts real sessions.
  Itemisation and plain disclosure are deliberate friction against approving without reading; they
  reduce that risk and do not remove it.
- Two surfaces can now clear the same gate, so both must stay correct. The mitigation is that both go
  through the same service and the same re-validating transaction — the duplication is in the UI, not
  in the rules.
- Deterministic retrieval cannot follow a thread. A question needing two searches gets one, and the
  Familiar must say so rather than guess.

### Neutral

- The action kinds must grow: a plan that starts an Implementer, and a way to say "this is already
  done", cannot be expressed by `CreateTask` and `StartPlanner`.
  `FamiliarConversationModelTests` asserts there are exactly two, deliberately, so a third cannot
  appear by accident. That test changes in the same commit as the kinds, with the reasoning recorded,
  or the kinds do not change.

## Constraint carried forward

Exactly one path from a proposal to persisted work, through `FamiliarActionService`, with gates
re-checked inside the executing transaction. Sprint 13 adds a second *shape* of proposal and a second
*place to stand*. It adds no second path.
