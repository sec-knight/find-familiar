# ADR-0010: Human-Gated Role Handoff

**Status:** Accepted

**Date:** 2026-08-05

---

## Context

Sprint 08 ended at a deliberate stop. A user describes work, reviews a deterministic proposal, approves
it, and exactly one Planner session runs. When that Planner finishes, nothing happens. ADR-0009 said
why, under the heading *Automatic role chaining stays deferred*:

> Sprint 08's whole claim is that a human approved *this specific piece of work*. Chaining a second
> role off a Planner result would silently extend that consent to work the user never reviewed. Role
> orchestration deserves its own explicit design and its own approval boundary.

That boundary is what this ADR designs. What remains after it is the ceremony a human still performs
between roles: read the plan, open the task, pick Implementer from a dropdown, start a session. The
decision in the middle of that sequence is real and worth keeping. The four steps around it are not.

Two other things had to move at the same time.

The one-Started-session-per-task invariant was still enforced only by an application-level read.
ADR-0005 recorded that as a deliberate trade-off and listed a partial unique index under its non-goals
because *"the sprint's explicit boundary is 'no schema change'"*. ADR-0008 restated the gap in its
negatives: *"The claim is now genuinely concurrency-safe at the database level, but session creation is
not."* Handoff approval adds a second concurrent session-creation writer, which is the condition those
ADRs were waiting for.

And ADR-0007 shipped `edit-worktree` mode *"implemented but not yet live-verified"*, unreachable from
automatic pickup because ADR-0008's configuration loader rejected every mode but `read-only`. An
Implementer that cannot write files produces a description of a change instead of the change.

## Decision

### A handoff is a durable record, not derived state

When a session reaches a terminal state, the transaction that ends it also stages a `SessionHandoff`
row: the task, the source session and its outcome, a proposed role, a kind, a status, an observed
context revision, and a concurrency token. It carries no free text.

The alternative — deriving "what should happen next" at read time from the sessions that already exist
— is where this started, and it fails on three counts. There is no row to consume conditionally, so a
concurrent approval has nothing to fence against and the loser gets a constraint violation instead of
an answer. There is nowhere to record that a human said *no*, so a declined step is indistinguishable
from an undecided one and the queue nags forever. And there is no audit of when consent was given.

Generalizing `WorkProposal` to cover both intake and handoff was rejected for a different reason. It is
one-to-one with `Conversation`, cascade-deleted from it, and carries a required title and requested
outcome. A handoff has no conversation, no title and no outcome, and there are many per task.
Generalizing means nullable columns, a dropped unique index, a discriminator, and re-filtering every
query in the three services Sprint 08's eight-way race proofs are written against — which is the
"two paths drift into conflicting invariants" failure ADR-0009 warned about, applied in reverse.

### The proposal is derived from role and status, and nothing else

| Source session | Proposal |
|---|---|
| Completed Planner | Implementer, `NextRole` |
| Completed Implementer | Reviewer, `NextRole` |
| Completed Reviewer | none |
| Cancelled, any role | the same role, `RetrySameRole` |

This is the ordering `WorkQueueService.DetermineAction` already derived advisorily; the handoff makes
it actionable and auditable without adding new role semantics.

It reads `Role` and `Status`. It never reads a summary, a raw output, or a review verdict. ADR-0005
rejected verdict parsing because *"it would make task progression depend on prose formatting a future
model revision could silently break, and would let a worker's own output implicitly complete a task
without human sign-off."* Nothing about that has changed. A completed Reviewer proposes nothing at all:
what happens to a reviewed task is a decision about the task, which stays with the human.

### Staging happens inside the transaction that ends the session

`ISessionHandoffService.StageHandoffAsync` is called from `SessionResultCaptureService` and
`SessionCancellationService`, after the context-revision increment and before the save. It supersedes
any handoff already pending on the task, then stages one.

That placement buys a database-guaranteed invariant: a terminal session that proposes a next step
always has exactly one handoff row. There is no window in which the queue shows a finished session with
nothing to approve. Deriving at read time would force either writes on GET — breaking ADR-0005's
statement that `/Work` performs no writes — or creation at approval time, which reintroduces the
check-then-insert race the fence exists to prevent.

The cost is real and worth naming: a fault in handoff staging now fails result capture, the most
safety-critical path in the system, and a worker's completed work would go unrecorded. Three properties
contain that, and all three are asserted:

- staging is **total** — every role and terminal-status pair either stages exactly one handoff or
  stages nothing, and nothing throws, unlike the artifact-kind mapping beside it;
- it issues exactly one query, the supersede update; and
- capture still returns success, writes exactly four context entries, and advances the revision by
  exactly one, per role.

The worker is unaffected. It posts a result; the server decides orchestration. It learns no new fact
and gains no new authority. `ContractVersion` stays 1.

### Approval is fenced, and three mechanisms do three different jobs

`SessionHandoffApprovalService.ApproveAsync` opens a transaction whose first statement is a conditional
update matching only a Pending handoff still carrying the reviewed token and still holding no created
session. It then re-reads the task and project authoritatively, checks for a Started session, starts one
ordinary session through `IWorkflowDispatchService`, saves, writes the durable link, and commits.

Distinguishing the three mechanisms matters more than any of them individually, because confusing them
is how the invariant gets deleted later by someone doing cleanup:

1. **The conditional update is authoritative for this handoff.** Exactly one contender can affect the
   row, so at-most-once consumption and replay-safety are chosen by the database, not by a read.
2. **The partial unique index is authoritative for the invariant.** `IX_AgentSessions_TaskId_Started`
   is what actually guarantees one Started session per task, across handoff approval, the manual start
   form, conversational approval and direct SQL alike. It is the only enforcement that does not depend
   on a caller remembering to check.
3. **The in-transaction Started check is a friendly pre-check.** It is reliable only because step 1
   already holds SQLite's write lock, so nothing can commit between the check and the insert. It exists
   to return a typed outcome instead of a constraint violation in the common case.

A loser leaves the handoff Pending with its original token, so an already-rendered button still works
later — the step is still valid, it just is not yet startable. A replayed approval returns the winner's
session, so a double-submitted button is inert.

Approval creates **no task**: the work already exists, only the role is new. It also leaves
`task.UpdatedUtc` alone, so all three session-creation paths have byte-identical effects.

### The handoff carries no content

`ContextProjectionService` already loads every active context entry for the task, and result capture
writes the source session's prompt, raw output, summary and artifact with `TaskId` set. The
Implementer's assignment packet therefore already contains the Planner's plan.

A notes field on the handoff would be a second, divergent copy of an artifact that already exists, and
a channel through which a human could inject instructions that bypass the context system's provenance.
A human with guidance to add records a context entry on the task, which the packet renders and which
correctly advances the revision.

### There is no revision gate on approval

Sprint 08 blocks approval when the observed project context revision no longer matches. That gate
protects *content the user authored* — a project, a title, a requested outcome — against context that
moved underneath it.

A handoff has no such content. The only decision is "run this role on this task now", and the session
created reads whatever context is current at its own start. A revision gate here would block on
activity in any other task in the same project, for no safety gain — cargo-culting the fence rather
than applying it. The observed revision is recorded and displayed as information; the real staleness
for a handoff is `Status != Pending`, which the fence enforces.

Revision arithmetic, unchanged where it was and explicit where it is new:

| Event | Δ revision |
|---|---|
| Conversational approval | +2 |
| Manual session start | +1 |
| Result capture | +1 |
| Session cancellation | +1 |
| Handoff creation | **+0** |
| Handoff approval | **+1** |
| Decline, supersede | **+0** |

Consent is not context.

### The migration repairs before it constrains

`CREATE UNIQUE INDEX` fails over data that already violates it, and the application migrates at
startup — so a database holding the state ADR-0005 tolerated would fail to boot. The migration
therefore normalizes first, in this order: write one cancellation context entry per losing Started
session; advance each affected project's revision by one; then cancel the losers, keeping the most
recently started and tie-breaking by id, the same ordering the work queue uses. All three statements
share a single predicate constant, so no session can be cancelled without its record and no record can
be written for a survivor.

The revision bump is deliberate rather than incidental: the surviving session read a revision that no
longer describes its task, and its assignment packet must show the stale-context warning.

The index creation is not wrapped in any error handling. A database holding `SessionHandoffs` without
`IX_AgentSessions_TaskId_Started` would run handoff approval against an unenforced invariant, which is
strictly worse than a startup that refuses to proceed.

`Down` drops the index and the table. **It cannot un-cancel the sessions `Up` normalized.** Their
cancellation context entries remain as the record of what happened. Full rollback of a live database
means restoring a backup, which is why taking one before the first run is a requirement and not a
suggestion.

No handoffs are backfilled for sessions that were already terminal. Backfilling would manufacture
pending consent rows for work the human already moved past.

### NeedsAttention survives the index

`WorkQueueActionKind.NeedsAttention` is now unreachable through any application write path. It is kept
anyway. The index exists only in databases that ran this migration, so a restored older backup can
still hold the violating state, and this is the only place it becomes visible. Its derivation is proved
on an isolated database where the index is dropped first, which reproduces that restore faithfully.

### Edit mode is unlocked, not widened

The worker configuration loader now accepts `edit-worktree`, and the mode is resolved per claimed role:
opting a project in does not make every session a writing one. A Planner is asked to plan and a
Reviewer to review, so neither is granted file writes even in an opted-in project. Only the Implementer
writes.

What edit mode permits is unchanged and still enforced by the adapter, not the worker: a clean linked
git worktree, whole-segment path containment with symlink resolution on every ancestor, a tool list of
`Edit,Write,Read,Grep,Glob`, and `Bash` deliberately excluded so there is no path to `git commit`,
`git push`, or any process execution from inside the model's turn. Sprint 09 changes *when* edit mode
is reachable, not *what* it allows.

Commits, branches, pushes and pull requests remain out of scope.

## Consequences

### What got better

The ceremony between roles collapsed to one click, and the click is still a decision: the human reads
the plan on the page that shows it, then approves or declines. Declining is terminal and recorded, so
the queue stops asking.

The one-Started-session-per-task invariant is now a property of the database rather than a property of
every caller's diligence. That closes the gap ADR-0005 and ADR-0008 both left open, and it does so at
the moment a second writer made it matter.

An Implementer now changes files.

### What we accepted

**Result capture depends on handoff staging.** The dependency runs the wrong way for comfort, and only
totality keeps it safe. It is asserted per role rather than assumed.

**A session no worker can claim now blocks its whole task.** Before the index, an unclaimable Started
session was untidy. Now nothing else can start on that task until it is captured or cancelled. This is
an operational cost paid for a correctness guarantee, and the guides state it plainly: a worker doing
automatic pickup must declare capabilities covering the roles it will be handed.

**`/Work` gained an action kind but no write handler.** Approving from a dashboard row would mean
approving work the human has not read, so the queue links to the task page instead. ADR-0005's
statement that the page performs no writes stands unamended.

**The index filter is a SQL literal.** `"Status" = 'Started'` matches only because `AgentSession.Status`
is stored via `HasConversion<string>()`. Removing that conversion would make the filter silently match
nothing, and the invariant would quietly disappear while every service check still called itself a
pre-check. `AgentSessionStartedUniqueIndexTests` asserts the database's behaviour directly so that
change cannot pass unnoticed.

**Edit mode is live for the first time.** ADR-0007 built it and never ran it against a real provider.
The proof for this sprint runs against a throwaway repository, not this one.

### What would justify pre-authorized advancement later

The obvious next step is letting a human authorize a whole plan — Planner, then Implementer, then
Reviewer — instead of each step. That is a real improvement to ceremony and a real move of the consent
boundary, and it should not be taken on the strength of it being convenient.

The condition for adopting it is evidence from this sprint's actual use:

- humans rarely decline a proposed step, so the per-step gate is not doing work that would be lost;
- live edit-worktree runs are predictable enough that a Pause between roles is a meaningful control
  rather than a hope;
- a cancelled session ends a plan rather than feeding a retry loop; and
- the plan remains finite, declared up front, never extended by the system, and still stops before task
  completion.

A pre-authorized plan that satisfies all four is a shorter path to work the human chose. One that does
not is a way to run work nobody reviewed — which is the thing ADR-0009 built its boundary to prevent,
and this ADR to keep.

## Alternatives Considered

### Deriving the next step at read time

Rejected. No row to fence against, no way to represent a declined step, and no audit of consent. See
above.

### Generalizing WorkProposal

Rejected. It would nullable-ise four columns and re-filter the services Sprint 08's concurrency proofs
target, to avoid a table with thirteen columns.

### Advancing automatically after a delay, with an undo window

Rejected. An undo window makes consent a race against a timer, and the guarantee degrades from "a human
approved this" to "a human did not object in time". The two are not the same claim, and only one of
them survives being asked about later.

### Relying on the index alone, without a handoff fence

Rejected. The index would produce a constraint violation for every loser, giving them an exception
where they need an answer, and would leave no record of which contender won or when consent was given.

### Enforcing the invariant with a trigger instead of an index

Rejected. A trigger is a second place for the rule to live, invisible to the EF model, and it would
have to reimplement exactly what a partial unique index already does.

## Non-Goals

- Automatic role advancement without a human decision, on any timer or trigger.
- Pre-authorized run plans.
- Automatic task completion.
- Any workflow decision derived from model-authored text.
- Commits, branches, pushes, pull requests, or deployment.
- A `Bash` grant or any widening of the adapter's tool list.
- A second queue, job type, runner route, or contract version change.
- Server-stored repository mappings or credentials.
- Provider routing or a second provider integration.
- Notifications, scheduling, or personality systems.
- Multi-user or public authorization.
