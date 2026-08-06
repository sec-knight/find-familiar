# Sprint 13 — The Familiar Runs the Work

Baseline: `main` @ `5052759` (Sprint 12 slices 1–3 shipped, 894 tests at last full run)

## Goal

One acceptance question:

> Can I ask the Familiar about a project, plan the next sprint with it, approve that plan **in the
> conversation**, and have it create the tasks, run the sessions, and fold the results back into what
> it knows — without me opening a task page?

When that works, the Familiar can be used to build the rest of what it needs.

## The correction this sprint is built on

An earlier draft of this plan had the human approving a plan on the Tasks page. That is backwards.

**The conversation is the control surface.** Every decision a person needs to make — approve a plan,
approve a handoff, accept a result, unblock something — is made in conversation with the Familiar.
The Projects, Tasks and Work pages remain, and become what they should always have been: places to
inspect and audit what happened, not the place work is driven from.

The Familiar owns the mechanics. Creating tasks, choosing roles, starting Planner, Implementer and
Reviewer sessions, giving each the context it needs, and harvesting what comes back — that is its job,
not the operator's.

## What already exists, and what is actually missing

Most of the machinery is built. The gap is narrower than it looks:

| Capability | State |
| --- | --- |
| Tasks, sessions, roles, handoffs | Built (Sprints 9–11) |
| Sessions receive assembled context | Built — assignment packets, ADR-0004 |
| Session results become context entries | Built — `SessionResultCaptureService`, ADR-0003 |
| Human-gated role handoffs | Built — ADR-0010, but approved on a task page |
| Proposals rendered inline in chat, confirmed there | Built for the per-project Familiar — ADR-0012 |
| Provider-neutral execution | Built — Runner, Claude Code adapter |
| **Familiar can read context entries and decisions** | **Missing** |
| **Talk lane can propose anything at all** | **Missing** |
| **Approval and handoff decisions in conversation** | **Missing** |

So this sprint is mostly connecting things that exist, plus the one genuinely absent piece: the
Familiar cannot read the reasoning it exists to preserve.

## Non-goals

- No autonomy. The Familiar proposes; a human approves; nothing runs unapproved. It is not given a
  standing mandate to create work.
- The talk lane still never writes to project state directly. Effects go through
  `FamiliarActionService`, which re-checks every gate inside the transaction that applies them. This
  sprint approaches that invariant deliberately and does not weaken it.
- No repository awareness — commits, diffs, build state. Still a sprint of its own.
- No plan spanning multiple projects.
- No new provider seams.

## Three properties that are structural from commit one

### 1. Every human gate is answerable in conversation

Not only plan approval. Pending handoffs, finished sessions awaiting acceptance, and blocked tasks all
surface in the conversation and are decided there. A gate that can only be cleared on a task page is a
gate that breaks the model this sprint is built on.

The task pages keep their controls. Two doors to the same decision is fine; the conversation being
the one that always works is what matters.

### 2. A plan is one proposal with many items, and approval is itemised

`IX_FamiliarActionProposals_ConversationId_Pending` already guarantees at most one undecided proposal
per conversation, and that is the right shape for a plan: contenders race for one row, a human decides
once, and a half-approved sprint cannot exist.

Approval is itemised because the blast radius is real. Each item can be excluded, each title and
outcome edited, and the card states plainly what will happen — how many tasks, which sessions start
immediately — before anything is clicked. The human's edits are what get created, not the model's
wording.

### 3. The loop closes through context, and the Familiar can see it

A session finishes, its result becomes a context entry, and that entry is retrievable by the Familiar
on the next turn. That is the whole point of the system, and until slice 1 it does not work: results
have been harvested since Sprint 9 into a store the Familiar cannot read.

## Slices

### Slice 1 — The Familiar can read its own context

Retrieval over context entries and decisions, merged into the pack. Server-side and deterministic
rather than a model-driven tool call (ADR-0014).

Retrieval that finds nothing enters the prompt as an explicit statement of that fact. Something that
cannot see will invent, and it invents most confidently when it does not know it is blind.

**Accept:** ask "why is the talk lane separate from the Runner?" and get an answer drawn from
ADR-0013's Decision entry rather than from a task title.

### Slice 2 — Citations that are checked

Inline markers streamed in prose, rendered as tappable chips, validated post-stream against the pack.
A marker naming an id that was never sent is unsupported and is styled so or dropped.

A plan is an argument about what to do next. An argument built on invented ids is worse than no plan,
because it is persuasive.

### Slice 3 — The Familiar drafts a plan

A durable multi-item plan proposal: several proposed tasks, each with a title, a requested outcome,
the role that should start, and the evidence it was drawn from. Drafted in conversation, persisted,
survives a reload. Nothing is created.

**Accept:** "help me plan the next sprint" produces a durable plan that appears identically on the
phone.

### Slice 4 — Approve it in the conversation

The plan renders inline in the transcript with per-item controls, exactly as the per-project Familiar
renders a proposal today. Approving creates every included task and starts **one** session — the
first — through `FamiliarActionService`, with every gate re-checked inside the applying transaction.

**Accept:** approving a drafted plan in the chat creates exactly the approved items with the human's
edits, starts exactly one session, and nothing else. Declining creates nothing. No task page is
opened at any point.

*Slices 1–4 are the sprint.*

### Slice 5 — The loop closes in conversation, one session at a time

A session finishes; the Familiar reports what came back and asks what to do next — start the next
item as planned, change it, or stop. Pending handoffs surface the same way: approving a Planner →
Implementer handoff in the conversation starts the next session. Accepting a result captures it, and
slice 1 makes it retrievable on the turn after.

The plan never drains on its own. Each step is approved on the evidence of the one before it, which
is the whole reason to run them one at a time rather than launching six on a guess (ADR-0014 §4).

**Accept:** a task goes from proposed to planned to implemented to reviewed; every human decision
along the way is made in the conversation; and the Familiar's account of each session cites the
context entry that session actually produced.

### Slice 6 — Warm open

A new conversation opens with the Familiar speaking first: where things stand, what needs a decision,
what has been sitting untouched.

## The action kinds — settled

They grow. `CreateTask` and `StartPlanner` are two because Sprint 11 shipped what it could reach, not
because two was right: neither can express starting an Implementer, nor "this is already done" — the
first thing actually asked of the Familiar the day it could talk.

`FamiliarConversationModelTests` asserts there are exactly two, and that guard stays. It is amended
in the same commit as the kinds it guards, with the reasoning recorded in ADR-0014, and it keeps
asserting an exact count afterwards. Growth is a decision; drift is what the guard forbids.

## Risks

**A plan is persuasive in a way a single action is not.** Six well-written tasks invite a glance and a
click, and approving now starts real sessions against a real repository. The itemised card and its
plain statement of consequences are deliberate friction against approving without reading. Friction
reduces this risk; it does not remove it.

**Retrieval quality is the whole sprint.** If the Familiar cannot find the right decision, the plan it
drafts is confident and wrong, and it will read well enough to be approved. Watch for plans that cite
nothing, or cite the same two entries every time.

**Grounding may cost conversational quality.** Sprint 12 already saw the Familiar go terse under
honesty rules alone. More evidence and citation requirements push further that way.

## What to watch

Which plans get approved unchanged and which get edited heavily. Heavy editing in one direction is the
next sprint's specification. Approving unchanged every time is a warning that nobody is reading.
