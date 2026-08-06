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

### 4. Approval creates the whole plan and starts exactly one session

Approving a plan creates every included task at once — the plan is the unit, and a half-created sprint
is the thing §3's single-row shape exists to prevent. But it starts **one** session, the first, and
then stops.

When that session finishes, the Familiar reports what came back in the conversation and asks what to
do next: start the next session as planned, change the next item, or abandon the rest. The plan is a
statement of intent, not a queue that drains on its own.

This is a deliberate narrowing of an earlier draft, which started every session the plan named on
approval. One at a time is better for reasons that are not only about caution:

- **A plan written before any of it ran is a guess.** The first session's result is the best evidence
  available about whether the second is still the right thing to do, and a plan that drains
  automatically throws that evidence away at exactly the moment it is worth most.
- **Six sessions started at once are six chances to be wrong before anyone reads anything.** Approving
  the *first* step is a smaller claim than approving six, and it is the claim a person can actually
  evaluate.
- **It makes the report the loop's heartbeat.** The Familiar has to come back and account for what
  happened before it gets to do anything else, which is the behaviour this system is supposed to have.

The remaining blast radius is handled by disclosure and itemisation rather than by an extra door:

- the card states plainly what will happen — how many tasks are created, and which single session
  starts now — before anything is clicked;
- each item can be excluded, and each title and outcome edited, with the human's version being what
  gets created;
- the applying transaction re-checks project status, observed context revision, and every per-item
  gate, refusing with the specific reason if the world moved;
- the existing single-started-session-per-task invariant still bounds what can run at once.

The cost is that a plan cannot run unattended, and is not meant to. If that becomes the constraint
that hurts, the change to make is a per-plan "continue without asking" the human sets deliberately —
not a default that drains.

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

- **The action kinds grow, deliberately.** `CreateTask` and `StartPlanner` are not two kinds because
  two was the right number; they are two because Sprint 11 shipped what it could reach. A plan that
  starts an Implementer, and a way to record that something is already done, cannot be said in that
  vocabulary — and the second was the very first thing asked of the Familiar the day it could talk.

  `FamiliarConversationModelTests` asserts there are exactly two, and that guard is doing its job:
  it makes growth a decision rather than a drift. It is amended in the same commit as the kinds it
  guards, with the reasoning recorded here, and it keeps asserting an exact count afterwards. What
  the guard forbids is a kind appearing because something needed one, not a kind existing.

## Constraint carried forward

Exactly one path from a proposal to persisted work, through `FamiliarActionService`, with gates
re-checked inside the executing transaction. Sprint 13 adds a second *shape* of proposal and a second
*place to stand*. It adds no second path.
