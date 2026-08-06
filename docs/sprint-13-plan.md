# Sprint 13 — The Familiar Plans

Baseline: `main` @ `0c20b82` (Sprint 12 slices 1–3 shipped, 894 tests at last full run)

## Goal

One acceptance question:

> Can I ask the Familiar what needs doing, work with it to shape the next sprint, approve the plan it
> drafts, and watch that become real tasks — without typing any of them myself?

When that works, the Familiar can be used to build the rest of what it needs, and every sprint after
this one is coordinated through the thing itself rather than around it.

## What Sprint 12 proved, and what it exposed

Sprint 12 ended with a Familiar that can hold a grounded conversation across devices and refuses to
act. Using it for one afternoon produced this sprint's specification more accurately than design
would have:

- **It cannot read the reasoning it is meant to preserve.** The standing brief carries projects and
  tasks. Context entries — including the eight Decision records describing why Sprint 12 is shaped
  the way it is — are not in it. The system built to preserve context cannot read its own context.
- **It cites ids nothing validates.** It emits raw task guids in prose. They happened to be correct.
  Nothing checked them.
- **It knows what should happen and cannot say so usefully.** Asked to close a completed task, it
  correctly refused and told the human to go do it by hand. That is right for a read-only lane and is
  a dead end for a person who wanted the work done.
- **Caps bite before retrieval does.** Eleven tasks against a cap of eight dropped the outstanding
  work until ranking was fixed. Ranking is a patch; retrieval is the fix.

## Non-goals

Explicitly deferred, and not to be pulled forward opportunistically:

- **The Familiar still never writes to project state.** Not once, not for a small change, not because
  a plan was approved. Every effect goes through a human confirmation and `FamiliarActionService`
  re-checking its gates inside the executing transaction. Sprint 13 approaches this invariant on
  purpose; it does not weaken it.
- **Approval creates tasks. It does not start work.** Starting a session spawns a real process
  against a real repository. That stays a separate, per-task confirmation using the machinery that
  already exists.
- No repository awareness — commits, diffs, build state. It is the Familiar's largest real gap and it
  is a sprint of its own.
- No plan spanning multiple projects. One plan, one project.
- No model-authored edits to an approved plan.
- No new provider seams; no second `IFamiliarChatProvider` implementation.

## Three properties that are structural from commit one

### 1. A plan is one proposal with many items

Not many proposals. `IX_FamiliarActionProposals_ConversationId_Pending` already guarantees at most
one undecided proposal per conversation, and that is exactly the shape a plan wants: contenders race
for one row, a human decides once, and there is never a half-approved sprint sitting in the database.

### 2. Every claim carries evidence, and the evidence is checked

A plan is an argument about what should happen next. An argument built on invented task ids is worse
than no plan, because it is persuasive. Citations are validated against the pack that produced them,
and an unsupported marker is styled as unsupported or dropped — never silently rendered as fact.

### 3. Approval is itemised, and the world is re-checked at the moment of approval

A plan creates many rows at once, which is a far larger blast radius than Sprint 11's single action.
So: each item can be excluded, each title and outcome is editable, and the transaction that applies
the plan re-validates every gate — project still active, context revision unmoved, no task already
running — exactly as a single confirmation does today. A plan approved against a world that has since
changed is refused, with the specific reason.

## Slices

### Slice 1 — The Familiar can read its own context

Retrieval over context entries and decisions, merged into the pack. This is the slice that makes
every later one possible: without it a plan is drafted from task titles and nothing else.

Deterministic, server-side retrieval rather than model-driven tool calls — see ADR-0014. The server
searches from the person's message and the conversation's focus, and what it finds enters the pack.

**Tool failure is surfaced, never swallowed.** A search that errors or returns nothing enters the
prompt as an explicit statement of that fact. The `--tools ""` lesson made structural: something that
cannot see will invent, and it invents most confidently when it does not know it is blind.

**Accept:** ask "why is the talk lane separate from the Runner?" and get an answer drawn from
ADR-0013's Decision entry, not from a task title.

### Slice 2 — Citations that are checked

Inline markers (`[[task:203]]`, `[[decision:…]]`) streamed in prose, rendered as tappable chips.
Post-stream validation against the pack: a marker naming an id that was never sent is unsupported.

**Accept:** a reply citing a real task links to it; a reply citing an id that was not in the pack
shows visibly as unsupported.

*Slices 1–2 are the foundation. Everything below is worthless without them.*

### Slice 3 — The Familiar drafts a plan

A durable `FamiliarPlanProposal`: several proposed tasks, each with a title, a requested outcome, and
the evidence it was drawn from. Drafted in conversation, persisted, and rendered for review. Nothing
is created.

**Accept:** "help me plan the next sprint" produces a durable plan of several tasks that survives a
reload and appears on the phone.

### Slice 4 — The human approves it, and it becomes real

Itemised review: exclude an item, edit a title or an outcome, approve the rest. Approval runs through
`FamiliarActionService`, re-checking every gate inside the transaction that applies it.

**Accept:** approving a drafted plan creates exactly the tasks approved, with the human's edits, and
nothing else. Declining creates nothing.

*Slices 1–4 are the sprint. Ship them and the acceptance question is answered.*

### Slice 5 — Carry it out

Start the first session on an approved task, using the existing per-task confirmation and the
existing Runner. The Familiar's involvement ends at the proposal; the DO lane is untouched.

### Slice 6 — Warm open

A new conversation opens with the Familiar speaking first: where things stand, what it would suggest,
what has been sitting untouched.

## The action kinds question

`CreateTask` and `StartPlanner` cannot express "this is already done" — the one thing actually asked
for on the day Sprint 12 shipped. A third kind, closing or completing a task, is the smallest addition
that removes the most common dead end.

`FamiliarConversationModelTests` asserts there are exactly two kinds, deliberately, so that a third
cannot appear by accident. Adding one is a decision to make in the open: update that test in the same
commit as the kind, or not at all.

## Risks

**Retrieval quality is the whole sprint.** If the Familiar cannot find the right decision, the plan it
drafts is confident and wrong, and a human will approve it because it reads well. Watch for plans that
cite nothing, or cite the same two entries every time.

**A plan is persuasive in a way a single action is not.** Six well-written tasks invite a glance and a
click. The itemised review exists to make approving without reading harder than reading.

**Grounding may cost conversational quality.** Sprint 12 already saw the Familiar go terse under
strong honesty rules. More evidence and citation requirements push further that way. If replies become
stiff and list-shaped, that is a prompt-shape problem before it is a model problem.

## What to watch

Which plans a human approves unchanged, and which they edit heavily. Heavy editing in one direction is
a specification for the next sprint. Approving unchanged every time is a warning that nobody is
reading.
