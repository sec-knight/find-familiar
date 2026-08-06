# ADR-0014: Conversational planning and multi-item plan proposals

- Status: Proposed
- Date: 2026-08-06
- Supersedes: none
- Related: ADR-0012 (human-confirmed action), ADR-0013 (talk lane / do lane split)

## Context

Sprint 12 delivered a Familiar that holds a grounded conversation and changes nothing. Asked on its
first day to close a task that was demonstrably finished, it refused correctly and told the operator
to do it by hand. That is the right behaviour for a read-only lane, and it is a dead end: the system
knew what should happen and could only describe it.

The goal for Sprint 13 is the point at which the Familiar can be used to build the rest of itself —
ask what needs doing, shape a sprint together, approve the plan, and have it become real work.

Two things stand in the way, and only one of them is about writing.

**The Familiar cannot read the reasoning it exists to preserve.** The standing brief carries projects
and tasks. Context entries are not in it. Sprint 12's own architectural decisions were recorded as
Decision entries and are invisible to the thing that recorded them. A plan drafted from task titles
alone is a plan drafted from almost nothing.

**A plan is not an action.** ADR-0012's proposal machinery carries one action with one target. A
sprint is several tasks that only make sense together, reviewed as a unit and approved once.

## Decision

### 1. Retrieval is server-side and deterministic, not a model-driven tool call

The Sprint 12 plan sketched `search_context(query, projectScope?)` as a tool the model calls. This ADR
chooses the simpler shape first: **the server searches, using the person's message and the
conversation's focus, and merges what it finds into the pack before the model is called.**

Reasons, in order of weight:

- ADR-0013 recorded that the chosen model's tool-calling reliability is provider-dependent and
  unproven. A tool that silently fails to fire produces an answer drafted blind, and a model that does
  not know it is blind invents most confidently. Deterministic retrieval cannot fail to fire.
- Search is a read. Model-driven tools earn their complexity when the model must decide *whether* to
  act; here it decides only what words to search for, and the person's own message is already a good
  query.
- One round trip per turn rather than two or three keeps the lane's latency budget, which is the whole
  reason it exists.

The cost is that the model cannot follow a thread — it gets one retrieval per turn and cannot refine
it. That is acceptable while conversations are short and the corpus is small, and the upgrade path is
open: adding model-driven search later does not invalidate anything built here, because either way the
result lands in the pack and the pack is what gets cited.

Retrieval that finds nothing enters the prompt as an explicit statement of that fact.

### 2. A plan is one proposal carrying many items

```
FamiliarPlanProposal            (one per conversation, undecided)
  conversation, project, status, concurrency token
  observed context revision
  drafted from: entity_refs[]
  items[]:
    title, requested outcome
    evidence refs[]
    included (human, default true)
```

One row, reusing `IX_FamiliarActionProposals_ConversationId_Pending`'s shape: at most one undecided
plan per conversation. Contenders race for one row, a human decides once, and a half-approved sprint
cannot exist.

The items are not tasks. They are a record of what a person will be shown, exactly as
`FamiliarActionProposal` is — not authority to act.

### 3. Approval is itemised, and every gate is re-checked inside the applying transaction

A single confirmation in Sprint 11 created one row. A plan creates several, which is a materially
larger blast radius, so the approval surface is deliberately more work to use:

- each item can be excluded;
- each title and requested outcome is editable, and the human's version is what gets created;
- the applying transaction re-checks project status, observed context revision, and every per-item
  gate, and refuses with the specific reason if the world moved.

Nothing about a plan bypasses `FamiliarActionService`. The invariant carried forward from ADR-0013
holds unchanged: **the talk lane never writes to project state.** It produces prose and proposals;
proposals are durable records; a human approves; execution runs through the one service that can
create work.

### 4. Approval creates tasks. It does not start sessions.

Starting a session spawns a real process against a real repository and consumes real provider
capacity. Approving a plan of six tasks must not start six of them.

Sessions stay a separate, per-task confirmation through the existing `StartPlanner` path. The DO lane
is untouched by this ADR, as it was by ADR-0013.

## Consequences

### Positive

- The Familiar can answer from recorded reasoning rather than from task titles.
- Planning becomes a conversation with a durable, reviewable artefact at the end of it.
- Every effect still passes through one human decision and one re-validating service.
- Retrieval has no dependency on provider tool-calling reliability.

### Negative

- A plan is persuasive in a way a single action is not. Six well-written tasks invite a glance and a
  click, and the itemised review is a deliberate friction against exactly that. This is a real risk
  that design can reduce and not remove.
- Deterministic retrieval cannot follow a thread. A question whose answer requires two searches gets
  one, and the Familiar must say so rather than guess.
- More evidence and stricter citation rules push the model further towards terse, list-shaped replies.
  Sprint 12 already saw this under honesty rules alone.

### Neutral

- A third action kind — closing or completing a task — is the smallest change that removes the most
  common dead end. `FamiliarConversationModelTests` asserts there are exactly two, deliberately, so a
  third cannot appear by accident; adding one is an explicit decision made in the same commit as the
  test change, or not at all.

## Constraint carried forward

Exactly one path from a proposal to persisted work, through `FamiliarActionService`, with gates
re-checked inside the executing transaction. Sprint 13 adds a second *shape* of proposal and no second
path.

The talk lane still holds no reference to `IWorkflowDispatchService`.
