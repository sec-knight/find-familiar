# Sprint 12 — The Familiar Speaks

Baseline: `main` @ `25472ed` (Sprint 11 accepted, 768/768, 0 warnings)

## Goal

One acceptance question:

> Can I open Find Familiar on my dev PC, my phone, and my tablet, and hold a single
> continuous conversation with something that knows my projects?

Nothing else in this sprint matters if that fails, and nothing else is worth designing
until it succeeds.

## Non-goals

Explicitly deferred, and not to be pulled forward opportunistically:

- proposals, approval UI, or any write to project state
- new action kinds; `FamiliarActionService` is not modified
- brief promotion, planner handoff, impasse records
- conversation compaction (cap working memory instead and let long conversations degrade)
- embeddings, vector search, local Ollama
- multi-session orchestration, parallel work, model routing policy
- persona work beyond a single editable system prompt

The Familiar sees everything and changes nothing. This is the largest available scope
reduction and it costs almost nothing: a read-only Familiar already beats copy-pasting
reports out of sessions.

## Two properties that are structural from commit one

Both are cheap now and expensive to retrofit.

### 1. System-wide scope

The route is `/Familiar` with no project id. Do not build the per-project version and
generalise later. Focus is a nullable, mutable attribute of the conversation that
affects retrieval ranking and pronoun resolution; it never restricts what the Familiar
can see. Cross-project questions are the point.

### 2. Server-owned continuity

The server is the conversation; the client is a window onto it. No state that matters
lives in the browser.

- Turns carry a monotonic sequence within the conversation.
- Generation is **detached from the requesting connection**. A turn starts generating
  and accumulates into the persisted record regardless of who is listening. Closing the
  laptop must not kill the response.
- One endpoint: give me everything after sequence N, then stream what follows. Same path
  whether the client has been gone four seconds or four hours, so the resume path is
  exercised constantly rather than rarely.
- At most one turn in flight per conversation, **server-enforced**. A second sender
  attaches to the in-flight turn rather than queueing. Same shape and rationale as the
  existing single-started-session invariant.
- Conversation list is server-side. Every device sees the same set.

## Slices

### Slice 1 — Durable conversation, no model

`FamiliarConversation` and `FamiliarTurn` entities with sequence and lifecycle state.
Migrations. Detached generation scaffolding. Resume-from-sequence endpoint. Server-side
conversation list. `/Familiar` renders a durable conversation.

**Accept:** conversation survives reload and a service restart, and appears identically
on a second device.

### Slice 2 — Provider and streaming

`IFamiliarChatProvider`, xAI implementation, SSE endpoint. Real streamed response, no
grounding yet.

**Accept:** tokens stream to the phone over Tailscale. Then the walk-out-the-door test —
start a response on the dev PC, pick it up on the phone in the driveway mid-stream.

### Slice 3 — Standing brief

The Demiplane projection serialised for a model instead of a human: every project,
health, open decisions, active sessions, work state. System-wide, bounded, ~2k tokens.

Prompt ordering is **stable to volatile** — system rules, persona, standing brief, then
evidence, history, user message — so the provider's prefix cache covers the stable head.

**Accept:** it answers "what's blocked?" correctly with no retrieval.

*Slices 1-3 are the sprint. Ship them and it is usable.*

### Slice 4 — One search tool

`search_context(query, projectScope?)` over context entries and decisions. Capped at two
calls per turn. Results merge into the pack — anything a tool returns becomes part of the
pack, or citation validation will flag legitimately fetched entities as hallucinations.

**Tool failure is surfaced, never swallowed.** A search that errors or returns nothing
enters the prompt as an explicit statement of that fact. This is the `--tools ""` lesson
made structural: a session that cannot see will invent, and it invents most confidently
when it does not know it is blind.

### Slice 5 — Citations

Inline markers (`[[task:203]]`) streamed in prose, rendered as tappable chips. Post-stream
validation: a marker referencing an id absent from the pack is unsupported and is styled
distinctly or dropped.

This is what turns "the Familiar should never present an unsupported inference as fact"
from a prompt one hopes holds into a check that runs.

### Slice 6 — Warm open

A new conversation opens with the Familiar speaking first from the standing brief: where
things stand, what it would suggest, what has been sitting untouched.

## Persisted turn shape

```
Turn
  sequence, state
  user_text
  focus_project_at_time
  pack: entity_refs[], revision_stamps
  provider, model, params
  accumulated_output
  claims[]: text, refs[], validation_result
  timings, token_counts
```

Storing the pack and params — not only the text — means any turn can be re-run against a
different model with byte-identical context. That is how "AI providers are replaceable"
gets tested rather than asserted.

## Sensitivity flag

The context assembler is the chokepoint through which everything leaves the machine.
Add a sensitivity flag on projects and context entries that the assembler honours, so
flagged content never enters a pack. Structural, not remembered. It is also what makes a
future local-model lane possible without redesign.

## Mobile

Not a footnote — the most likely place this sprint fails, and the least likely to get
attention because it is not architectural.

- SSE connections drop on wifi/cellular handoff. Streams reconnect and resume by turn id.
- iOS Safari suspends tabs mid-stream. A suspended tab wakes far behind and must recover
  by fetching the gap; an out-of-date device that looks current is worse than one that
  obviously needs a refresh.
- The composer stays put with the transcript scrolling under it. Safe-area insets handled.
- Citation chips are tap targets, not inline text links.

Test on the actual tablet over the actual Tailscale connection from the first slice that
renders anything. Bookmark the MagicDNS name, not `100.80.43.11`.

## Cost

~$0.003 per turn on `grok-4.1-fast`. $5 covers well over a thousand turns. Cost is not a
constraint on this sprint and should not shape any decision in it.

## What to watch

Which questions the standing brief answers well, and which fail because retrieval could
not find something. That list is the Sprint 13 specification, and it will be more accurate
than anything designed in advance.

Also watch whether Grok's conversational quality survives grounding. A model that is
pleasant in open chat can go stiff when handed structured evidence and told to cite. If
that happens it is a prompt-shape problem before it is a model problem.
