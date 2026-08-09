# ADR-0020: A plan a human approves must be retrievable in full

- Status: Accepted
- Date: 2026-08-09
- Related: ADR-0003 (atomic session result capture), ADR-0006 (provider-neutral runner bridge),
  ADR-0007 (Claude Code worker adapter), ADR-0010 (human-gated role handoff), ADR-0016 (the
  Summoning Gate), ADR-0019 (peer frontends over one authoritative state)

## Context

ADR-0010 put a human between a finished Planner session and the Implementer session it proposes.
The human approves or declines; nothing runs until they do. That gate is the system's central
safety property, and everything else about automatic pickup is built on the assumption that it is
real.

It was not real. It could not have been.

The Planner's artifact reached the database through a chain of bounds, each individually
defensible:

1. `ClaudeResultParser` cut the provider's result to `MaxLongFieldLength` (12,000 characters) and
   appended `[truncated by adapter]`.
2. `RunnerEngine` validated that every result field was at or below that bound.
3. The runner result endpoint accepted at most 64 KB of request body.
4. `SessionResultCaptureService` refused any artifact longer than 12,000 characters.
5. `ContextEntry.Content` was a 12,000-character column.

The first of those is the one that mattered: the cut happened **before anything stored anything**.
The remaining four then made it impossible to fix by passing a longer string along the same path.

The consequence, measured against the live database at commit `d671d84`: of eight stored `Plan`
artifacts, seven were exactly 12,000 characters long and each contained exactly one
`[truncated by adapter]` marker. Every substantial plan this system has ever produced was approved,
or declined, by a human who could not have read it. The remainder had not been withheld, or paged,
or archived. It had never existed anywhere.

A later sprint added paged retrieval — `GetSessionHandoffPlanAsync` with `Offset`, `TotalLength`,
`HasMore`, an MCP `get_session_handoff_plan` tool and a REST twin — and it worked correctly. It
paged faithfully over a 12,000-character string, reported `hasMore: false` at the end, and told the
caller it had shown "a complete bounded Planner artifact". Every word of that was true about the
stored record and false about the plan. The mechanism was sound; there was simply nothing behind it.

That is the specific failure this ADR exists to prevent: **a bound applied before storage, reported
downstream as completeness.**

## Decision

### The invariant

> A decision a human is asked to make must be presentable together with the complete artifact it
> authorizes. An approval boundary may bound what it *displays*; it may never be the only place a
> reviewable artifact exists, and it may never describe a bounded view as a complete one.

### Three consequences

**1. The excerpt stops being the only copy.**

The complete artifact travels with the result (`CompleteArtifactContent`, plus
`CompleteArtifactLength` — the length *before* any bound) and is stored in `ContextEntryArtifacts`,
one row per context entry, bounded at 200,000 characters. `ContextEntry.Content` keeps its 12,000
bound and its meaning: a cheap excerpt for lists, briefs and retrieval budgets. The change is not
that the excerpt got bigger. It is that the excerpt is now an excerpt *of* something.

The artifact is stored verbatim, where the excerpt is trimmed. An artifact under approval must be
byte-exact, and trimming it would both edit it and make its retained length disagree with its
declared one.

**2. Completeness is a value, not a tone of voice.**

`FamiliarPlanCompleteness` distinguishes four states a page of text cannot distinguish on its own:

| Value | Meaning | What the reader should do |
| --- | --- | --- |
| `Complete` | The whole artifact, in this response | Approve or decline |
| `Page` | Part of a stored whole artifact | Keep paging |
| `PartiallyRetained` | Exceeded the retention bound; the shortfall is known in characters | Do not treat as whole |
| `Excerpt` | Only a bounded excerpt was ever stored | Do not treat as the plan |

`IsWholeArtifactRetrieved` collapses these into the single flag an approval flow should gate on, so
"have I read all of it?" is answered mechanically rather than by inspecting text for an ellipsis.

The last two states are the honest half. Plans captured before this change genuinely have no
remainder, and the system now says so plainly instead of inviting a caller to page toward text
nobody stored.

**3. Both frontends can reach it (ADR-0019).**

The Summoning Gate pages it through `get_session_handoff_plan` and `GET /api/gateway/handoffs/{id}`;
the Demiplane pages it at `/handoffs/{id}/plan`, linked from the approval box itself. Neither
frontend is structurally incapable of showing the human what they are approving.

### What did not change

- **Sensitivity filtering.** An artifact row has no visibility rules of its own and is reachable
  only through its entry, so the entry's `IsSensitive` flag and kind decide who may see it. A
  document fetchable without its entry would be a second answer to "may this be shown".
- **Raw provider prompts and output** remain excluded from every external answer.
- **The read-only shape of the plan path.** The retrieval surface gained no mutation, and the
  Demiplane's plan page holds no capture, dispatch or approval service — the page that informs a
  decision cannot make one.
- **Concurrency fencing.** Approval still runs against the handoff's concurrency token, on the task
  page, unchanged.

### Bounds that moved, and why

| Bound | Was | Now | Reason |
| --- | --- | --- | --- |
| `MaxCompleteArtifactLength` | — | 200,000 | New: what may be retained whole |
| `MaxAdapterOutputBytes` | 256 KB | 1 MB | A complete artifact made the adapter's own output "oversized" |
| `MaxRequestBodyBytes` | 64 KB | 1 MB | A complete artifact made the result body "oversized" |

None of these became unbounded. An artifact beyond the retention bound is kept up to it and reports
its true original length, so the shortfall is stated rather than hidden.

## Consequences

- Sessions that ran before this change are permanently incomplete, and now say so. They cannot be
  repaired; the text was never captured.
- A worker running an older adapter still captures successfully, and its artifacts are reported as
  `Excerpt` rather than silently treated as whole.
- The Planner → Implementer gate becomes a real gate for the first time.

## The distinction this exposed, and deliberately did not build

Fixing this surfaced a conflation worth naming, because the fix is only half of what the system
eventually needs.

- **A session plan** is a bounded execution proposal for one task, session and handoff. It answers
  *what exactly are we proposing to do next?* It is an approval artifact, it is what this ADR makes
  reviewable, and once decided it is history.
- **A project planning space** is a durable, evolving body of project intent above any individual
  task or session: vision and purpose, roadmap and milestones, architecture, domains and
  boundaries, constraints, accepted design decisions, open questions, deferred ideas, and the
  history of how all of that changed. It should behave like a project's content bible rather than
  an accumulation of old AI transcripts.

Today Find Familiar has the first and only an implicit, scattered version of the second — spread
across context entries, ADRs and sprint documents. A Planner cannot reliably consume project intent,
and has nowhere to contribute a discovery back to.

That layer is **not built here**, on purpose. It is a design problem large enough to deserve a
Planner session and a human review of its plan — which is precisely the thing that was impossible
before this ADR. Building it in the same session that made planning reviewable would have skipped
the review it exists to enable.

The smallest future implementation boundary: a project-scoped planning document set, separate from
`ContextEntry`, with revisions and supersession, that the assignment packet reads from and a
session can propose changes to through the existing human gate. See the durable Open Question
recorded against the Find Familiar project.
