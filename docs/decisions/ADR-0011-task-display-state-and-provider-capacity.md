# ADR-0011: Task Display State and Provider Capacity

**Status:** Accepted

**Date:** 2026-08-05

---

## Context

Through Sprint 09 the software could run work but could not explain itself. Understanding a project
meant reading `/Work`, opening a task, expanding its sessions, and often querying the database —
`TaskStatus`, `AgentSessionStatus`, claim columns and handoff rows each told part of the story and
none told it whole. The states that exist are execution authority, chosen to be unambiguous for the
claim and capture paths, not to be read by a person deciding what to do next.

Sprint 10 introduces the Demiplane, a per-project command surface answering four questions: where are
we, what is happening, what needs a human, and what should happen next.

Two problems had to be solved honestly before any of it could be drawn, and both turned out to be
about what the system does *not* know.

## Decision

### Display state is derived once, in the application layer

`DemiplaneProjectionService` owns every rule about what a task shows and why. Razor renders the
result and derives nothing.

The alternative — asking each view to interpret `TaskStatus`, session rows and handoffs — would have
produced two interpretations immediately, because the desktop map and the mobile trail are separate
markup over the same data. Sprint 09's `IWorkflowDispatchService` extraction was made for the same
reason: two copies of a rule is how they come to disagree.

Eight states, each carrying a reason code and human-readable text:

| State | Meaning |
|---|---|
| Not Started | No session has ever run |
| Waiting | Progress depends on something else; see the reason |
| Running | A session is claimed and executing |
| Needs Attention | A human decision is outstanding |
| Blocked | Nothing can proceed until a human intervenes |
| Succeeded | A human marked the task complete |
| Failed | A session ended because something went wrong |
| Cancelled | A human deliberately stopped the work |

A bare state is not an explanation, so a reason code always accompanies it. "Waiting" is never shown
without saying what for.

**This is display state, not execution authority.** Nothing consults it to decide what may run. The
claim service, capture path and handoff fence are untouched by Sprint 10 and do not know it exists.

### The one distinction that earns its complexity

An unclaimed Started session can mean two very different things, and conflating them was the failure
mode most likely to waste someone's afternoon:

- if some enabled worker declares that role, the task is **Waiting** — ordinary queueing, nothing to do;
- if none does, the task is **Blocked** and asks for a human, because ADR-0010's uniqueness index
  means that stuck session also prevents anything else starting on the task.

This is the operational trap ADR-0010 named in its consequences. Surfacing it is the difference
between a user noticing in seconds and noticing in days. Worker capabilities are read only to explain
the situation; they gate nothing, and remain self-reported (ADR-0008).

### Failure categories come only from strings this codebase writes

`SessionOutcomeClassifier` recognises exactly one thing: the fixed diagnostic reasons the runner
itself records when it cancels a session — `adapter-launch-failed`, `adapter-timeout`,
`adapter-non-zero-exit`, and the three malformed-output categories. ADR-0006 fixed those strings
deliberately and they contain no secrets and no model output.

Everything else is left unclassified. A reason a human typed is shown verbatim and never interpreted.
Nothing is inferred from a summary, a raw output, or a review verdict — ADR-0005 rejected exactly that
for the work queue, on the grounds that it would make progression depend on prose a model revision
could silently change. A Demiplane that guessed "tests failed" from a summary would be confidently
wrong at precisely the moment someone trusted it.

The visible cost: **this project persists no structured build or test result, so the Familiar never
claims one.** The task panel states that plainly rather than leaving a suggestive blank.

An unrecognised machine category is still a failure — it was machine-recorded — but reports Unknown
rather than inventing a cause.

### A decision is only ever read from a persisted decision

The first implementation of this ADR got the corollary wrong, and review caught it. When the latest
session had completed and no *pending* handoff existed, the page reported that the proposed next step
"was declined" — inferring a human decision from the absence of a row.

That is false for every task completed before Sprint 09, because ADR-0010's migration deliberately
backfills no handoffs. Two tasks in the real database would have displayed a decline nobody made.

The projection now reads **all** handoffs for a task and scopes the decision to the one recorded
against the latest terminal session:

| Recorded state | Reported as |
|---|---|
| Declined | the decline, naming the role |
| Approved or Superseded | a decision exists, without characterising its effect |
| no row at all | "finished and no next step is currently proposed" |

The general rule this makes explicit: **absence of a record is not evidence of an event.** It applies
to more than handoffs, and it is the same discipline that keeps failure categories tied to strings we
wrote ourselves.

### Capability is not a promise of pickup

The same review found the inverse error. The server knows only that some enabled worker *declares* a
role; repository mappings are machine-local and never reported to it (ADR-0008). A worker declaring
Implementer with no mapping for this project will never claim the session.

The page therefore no longer says "a worker should claim this shortly". It states what is known and
what is not: a worker declaring the role is available, and it can claim the work only if that machine
has a local mapping for this project. The Blocked wording is unchanged, because "no enabled worker
declares that role" is directly verifiable.

### The Familiar's summary is assembled, not generated

Each task leads with plain language: what happened, what is happening now, why it is waiting, what
needs a human, and what to do next. Every sentence is composed from persisted facts — which sessions
exist, their roles, their outcomes, the durable cancellation reason.

No model is called to write it. A generated summary would be a second, unreviewable account of work
whose real record already exists, and it would be wrong in the same undetectable way a guessed failure
category would be. Where the data cannot support a statement the field is null and the page says so.

### Provider capacity is an abstraction over an absence

`IProviderCapacityReader` isolates provider-specific collection; `ProviderCapacityService` aggregates
readers, bounds each with a timeout, and converts a throw into a visible Unavailable entry. Six
statuses — Available, Constrained, Low, Exhausted, Unknown, Unavailable — with confidence and source
on every reading.

**The reader shipped for Claude reports Unknown, always.** That is not a stub. It is what investigation
found:

- The Claude Code CLI this project invokes exposes no non-interactive usage surface: no `usage`
  subcommand, no `--limits`, `--quota` or `--cost` flag. `/usage` is interactive-only and produces no
  machine-readable output.
- The adapter's JSON envelope carries no capacity data, and `ClaudeResultParser` deliberately discards
  everything except `is_error`, `result` and `permission_denials` (ADR-0007).
- Nothing cached under the user's Claude directory carries a limit, a window or a reset time. Local
  transcripts record token *consumption* with no denominator, which is not capacity.
- There is no Codex or OpenAI integration in this repository at all — one line of README marketing and
  zero code.

Two tempting sources were rejected. Estimating remaining capacity from local token counts would put a
number on screen that no provider reported; the interface exists specifically to prevent presenting an
estimate as a balance. And a real `rate_limits` block does exist on this machine, written by the Codex
CLI — but it belongs to a different tool, describes a provider this application never invokes, and was
days stale. Displaying it as Familiar's provider readiness would be fabrication wearing the costume of
real data.

So every quantitative field is nullable, and the strip says "Unknown" with the reason attached.

### Provider exhaustion is never an implementation failure

`WaitingForProviderCapacity` exists as a reason code and can never map to Failed. A capacity limit is a
scheduling condition: the implementation was fine, the allowance ran out.

**It is not currently reachable from live data, and that is stated rather than hidden.** A quota
rejection today arrives as a non-zero adapter exit, indistinguishable from any other provider error,
and surfaces as `ProviderRequestFailed` — whose own text says the adapter cannot yet tell exhaustion
apart. Modelling the state without pretending to detect it keeps the shape ready and the claim honest.

### Refresh is bounded and conditional

A `meta refresh` at 30 seconds, contributed to `<head>` through a layout section and emitted **only**
when a task is running or waiting. A settled project
never refreshes, so someone reading a summary is not interrupted and no request is made that nobody
needs. It is a plain page reload: it triggers no work, preserves no partial form state, and cannot
approve anything.

This keeps the application's no-JavaScript property, which is not nostalgia — it is why the whole UI
works over a phone browser on a tailnet with no build step, no bundle, and no client-side state to
desynchronise.

### Desktop and mobile are one projection and one markup

The map is a CSS grid that collapses to a single-column vertical trail below 48rem. The phone gets a
focused list, not a squeezed canvas, and both consume the identical projection — so the two views
cannot disagree about what is true.

No graph library was introduced. The domain has no task-to-task relationships (see below), so there is
no graph to lay out; a canvas library would have been a large fragile dependency rendering a list.

### The map draws no edges, because there are none

`FamiliarTask` holds `Id`, `ProjectId`, `Title`, `RequestedOutcome`, `Status` and timestamps. There is
no dependency, parent, ordering or blocking relationship anywhere in the domain.

The map therefore shows the one real graph structure that exists — each task's session chain, plus its
proposed next step as an explicitly un-started node — and states plainly that tasks are listed
independently because the project records no dependencies between them.

Inventing edges from creation order or shared conversations would have produced a diagram that looked
authoritative and meant nothing.

## Consequences

### What got better

A project can be understood from a phone without a terminal. Every waiting and blocked task explains
itself. The one operational trap ADR-0010 introduced is now visible instead of latent. And approving a
proposed step no longer requires navigating to the task page.

### What we accepted

**Two derivations read the same rows.** `WorkQueueService` answers "what is the next action" and the
Demiplane answers "what is true"; they are related but not identical questions, and keeping them
separate avoided contorting a tested Sprint 05 service. The drift risk is real and mitigated by
`DemiplaneWorkQueueConsistencyTests`, which pins the states they share.

**The readiness strip currently shows one Unknown.** A strip that admits it knows nothing is worth
having anyway: it names the provider, and it is where real data will appear without another UI change.

**Blocked is used for two different situations** — a human marked the task blocked, and no worker can
run its role. The reason code distinguishes them; the state does not.

**Non-ASCII markers render as numeric entities.** Harmless in a browser, but tests must decode before
asserting. Noted so the next person does not think the markers are missing.

### What would justify revisiting

A **live provider capacity reader** becomes possible the moment any of these appears: a scriptable
usage command on the Claude CLI, a documented usage field in the adapter's envelope, or a decision to
call the provider's API directly with its own credentials. The abstraction is already shaped for it —
what must not change is that a reader which cannot determine a value returns Unknown rather than an
estimate.

**Structured build and test results** would let the Familiar say "all tests passed" truthfully. That
requires the runner contract to carry them and the schema to persist them, which is a contract-version
change (ADR-0006) and deserves its own decision.

**Task dependencies** would make the map a real graph. That is a schema change plus an editing surface
to populate it, and it should be driven by an actual need to express one task blocking another — not
by the map looking sparse.

## Alternatives Considered

### Deriving display state in Razor

Rejected. Two views over one dataset would have produced two interpretations, and none of it would
have been testable without rendering HTML.

### Extending WorkQueueService instead of adding a projection

Rejected. It answers a different question, is scoped across all projects, excludes completed tasks, and
carries Sprint 05 tests asserting its current shape. Bending it to serve both would have made one
service worse at both jobs.

### Reading the Codex CLI's rate-limit file

Rejected outright. Real data about the wrong tool is still misinformation.

### Estimating capacity from local token transcripts

Rejected. Consumption without a limit is not capacity, and an estimate displayed beside a provider name
reads as a provider-reported balance.

### A client-side graph library

Rejected. There is no graph in the domain to render, and it would have cost the no-JavaScript property
that makes the phone experience work at all.

### Polling with fetch, or SignalR

Rejected for this sprint. A conditional 30-second meta refresh is the smallest thing that keeps a
running project current, and it needs no client-side state, no reconnection logic and no new
dependency. Real-time updates would need a latency requirement to justify them, which ADR-0008 also
noted when it declined push.

## Non-Goals

- Display state as execution authority.
- Generating summaries with a model.
- Inferring outcomes from model-authored text.
- Presenting estimated capacity as provider-reported.
- Fabricating task dependencies.
- A native mobile application.
- Drag-and-drop, user-positioned nodes, or an infinite canvas.
- Automatic provider routing or cost prediction.
- Any change to the runner contract, the claim path, or the Sprint 09 approval fence.
