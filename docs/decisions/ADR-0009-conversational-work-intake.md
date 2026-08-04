# ADR-0009: Conversational Work Intake and Explicit Approval

**Status:** Accepted

**Date:** 2026-08-04

---

## Context

After ADR-0008, Familiar could execute work without a human relaying GUIDs. What it still could not
do was *accept* work in ordinary language. Creating something for a worker to run meant:

```text
Choose project -> create task -> open task -> choose Planner -> start session -> wait
```

Six deliberate steps, every one of them a form. The information the user actually has — "plan the
next slice of Find Familiar, keep it small" — had to be manually decomposed into a project, a
title, a requested outcome and a role before anything could happen.

The obvious way to close that gap is also the dangerous one: hand the sentence to a model, let it
decide what the work is, and start it. That would put an unreviewed model call *before* the point
where the user consents to anything, and it would make the first AI action in the system one the
user never saw or approved.

Sprint 08 closes the gap while keeping consent where it belongs.

---

## Decision

### Proposals are deterministic, and they are the only thing that exists before approval

Familiar turns the request into a structured proposal using pure, testable rules
(`DeterministicProposalGenerator`) — no model, no provider, no network:

- **Project.** Exactly one active project whose complete normalized name occurs in the request wins.
  With no name match and exactly one active project, that project is proposed. Anything else stays
  unresolved and the user chooses. Multiple matches are *never* silently narrowed.
- **Title.** The first non-empty line, bounded to 200 characters on a text-element boundary so an
  emoji or combining sequence is never cut in half.
- **Requested outcome.** The full trimmed request, bounded to 4,000 characters.
- **Role.** Always Planner.

Matching is ordinal and culture-invariant, over candidates loaded into memory, so it cannot inherit
a database collation quirk or a server locale.

This is deliberately modest. A deterministic proposal is one the user can *check* — they can see
exactly why Familiar chose that project, and re-running the same request gives the same answer.
A model-generated proposal would be better prose and worse consent.

Intake writes to exactly three tables: `Conversations`, `ConversationMessages`, `WorkProposals`.
It creates no task, no session, no context entry; it does not move a project's context revision, it
does not notify a worker, and it does not call a provider. That boundary is asserted directly
against the database rather than inferred from code review.

### The first provider call is the Planner session the user approved

Approval creates an ordinary Ready task and an ordinary Started Planner session. Nothing downstream
knows a conversation was involved: the work queue, assignment projection, claim service, runner,
adapter and result-capture service see exactly what a manually started session produces. There is
no conversation job type, no second queue, no new runner route, and `ContractVersion` stays 1.

The proof is an end-to-end test in which a worker configured with a project mapping — and no task
or session identifier — discovers and executes work that began as a sentence.

### Conversation status is not workflow authority

`Conversation.Status` and `WorkProposal.Status` record what the user asked for and whether they
approved it. They are display and audit state.

Whether a session may be claimed, captured or cancelled remains decided by `AgentSession.Status`,
`TaskStatus`, context revisions and claim ownership — exactly as ADR-0005, ADR-0003 and ADR-0008
established. No worker-facing code path reads a conversation.

Keeping these separate is what stops the conversation layer from becoming a second, competing
state machine over the same work.

### Approval is fenced and atomic

`WorkApprovalService.ApproveAsync` is one transaction whose **first** statement is a conditional
`UPDATE`:

```sql
UPDATE WorkProposals
   SET Status = 'Approved', ConcurrencyToken = <new>, UpdatedUtc = <now>
 WHERE Id = @id
   AND Status = 'Pending'
   AND ConcurrencyToken = @reviewedToken
   AND CreatedTaskId IS NULL
   AND CreatedSessionId IS NULL
```

Two properties follow, and both are required:

1. **The database chooses the winner.** Exactly one contender can affect a row. A preflight read
   followed by an ordinary tracked insert would let several contenders pass inspection and all
   dispatch — that is the specific race this shape exists to prevent. The `CreatedTaskId IS NULL`
   clause means even a leaked token cannot dispatch twice.
2. **One transaction covers the winner's complete effects.** The task, the session, both
   context-revision increments, the durable links and the visible message commit together. An
   injected failure after the consume leaves no task, no session, no revision drift, and a proposal
   still Pending — proved by a test that fails the transaction at exactly that point.

Writing conditionally *first* also matters for SQLite specifically: the transaction takes its write
lock immediately instead of upgrading from a read, which is the shape that deadlocks rather than
waiting politely under the busy timeout.

Losing contenders are told the truth. `AlreadyApproved` returns the links the winner created, so a
double-submitted form is inert rather than an error; a genuine conflict is reported as one. No
concurrency exception is swallowed and reported as success.

Revision, refresh and rejection use the same fence, so a stale form cannot overwrite a newer
revision and a rejected proposal cannot be revived.

Concurrency is proved on real file-backed SQLite with independent contexts and connections — eight
contenders released together by a barrier, plus approval-versus-rejection and revision-versus-
approval races. An in-memory provider shares one store and one change tracker and would prove
nothing about the serialization this design depends on.

### Context revision semantics are preserved exactly

Approval reuses `IWorkflowDispatchService`, the same seam the manual pages now use. Task creation
advances the project's revision once; starting the session advances it again and records the
revision the session actually reads. Approval therefore produces `+2` and
`ContextRevisionRead == final` — identical to creating a task and starting a session by hand.

Extracting that seam was the point: two copies of those rules is precisely how manual and
conversational creation would drift into conflicting invariants.

A proposal stores the project context revision observed when the user reviewed it. If the project's
context has moved on, approval **stops**. The user refreshes — which records the new revision and
rotates the token, invalidating every already-rendered approve button — and reviews again. Work is
never dispatched against context the user never saw.

### Automatic role chaining stays deferred

Approval dispatches Planner and stops. No Implementer or Reviewer session starts on its own, and no
keyword inference picks a role. Task completion remains a human decision.

Sprint 08's whole claim is that a human approved *this specific piece of work*. Chaining a second
role off a Planner result would silently extend that consent to work the user never reviewed. Role
orchestration deserves its own explicit design and its own approval boundary.

---

## Consequences

### What got better

The six-step ceremony collapses to: describe, review, approve. The user still decides what runs;
they just no longer have to hand-compile the request into four form fields first.

### What we accepted

- **The proposal is literal, not clever.** It cannot infer that "the auth thing" means a particular
  project, split one request into several tasks, or write a better title than the user's first line.
  That is the cost of being checkable.
- **Ambiguity requires a click.** With several projects and no name match, the user must choose.
  Guessing would be a worse failure than asking.
- **One proposal per conversation.** A conversation is intake for one piece of work, not a thread.
- **Terminal is terminal.** An approved or rejected conversation never reopens. Wanting different
  work means describing it again — which is also a clean audit trail.

### What would justify an AI-assisted proposal generator later

The deterministic engine will visibly run out of room when: projects are routinely referred to by
nickname or implicitly; one request genuinely describes several tasks; or the first line is a poor
title often enough that users always revise it.

The condition for adopting one is not that it would produce nicer proposals. It is that the
approval boundary in this ADR stays exactly where it is:

- generation must remain a *suggestion* the user reviews and edits before anything exists;
- the generated proposal must still be a plain, structured, editable record — not an opaque plan;
- a generation failure or timeout must degrade to today's deterministic proposal, not block intake;
- generation must not read anything the user could not see on the page; and
- approval must remain the single fenced, atomic transaction described above.

An AI proposal generator that satisfies all five is an improvement to step one. One that does not
is a way to start work nobody approved.

---

## Alternatives considered

**Call a model during intake to produce the proposal.** Rejected. It puts an unreviewed provider
call before consent, makes proposals irreproducible, and couples the intake page to provider
availability, latency and cost. The approved Planner session already provides project-aware
intelligence — after the user says yes.

**Create the task at intake and mark it "draft/unconfirmed".** Rejected. A `Draft` task is still a
real row in a real table that other code can find, and it would make "did the user approve this?"
a property to check everywhere instead of a boundary crossed once.

**A dedicated conversational queue or job type.** Rejected outright. It would duplicate the claim,
lease, fencing and capture semantics ADR-0003/0005/0006/0008 already prove, and guarantee the two
paths drift.

**Optimistic concurrency via an EF `[ConcurrencyToken]` on the tracked entity alone.** Rejected as
insufficient by itself. It protects a field update, but approval must also guarantee that only one
contender proceeds to *insert* a task and a session. The conditional `UPDATE ... WHERE Status =
'Pending' AND Token = @t` inside the transaction is what makes the winner unique; the token is the
fence, not merely a version check.
