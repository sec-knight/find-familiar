# Architecture: the spirit and its bodies

> Find Familiar owns durable continuity. External AI clients are interchangeable embodiments of it.

This is the framing the system is built around. It is worth stating plainly because it is easy to
mistake this project for a chat application with a database behind it, and the design only makes sense
the other way round.

## The metaphor, and where it is literal

**The Familiar spirit** is what persists: identity, project knowledge, decisions, recorded context,
sensitivity rules, what is running and what is waiting on a person. It lives in this server and in
SQLite beside it. It is authoritative.

**A body** is whatever is currently speaking as the Familiar — ChatGPT on a phone, Claude, a local
model, the native Razor pages. A body has the voice, the latency and the ergonomics. It has no memory
of its own that matters, and it is replaceable.

**Changing bodies must not cost the Familiar its continuity.** That single requirement produces most
of the decisions below.

## The layers

```
  ┌─────────────────────────────────────────────────────────────┐
  │  Demiplane — authoritative state                            │
  │  projects, tasks, sessions, handoffs, context entries       │
  │  deterministic; the only thing that says what is true       │
  └────────────────────────┬────────────────────────────────────┘
                           │  read
  ┌────────────────────────┴────────────────────────────────────┐
  │  Familiar identity and context                              │
  │  standing brief · retrieval + relevance floor · sensitivity │
  │  repository snapshot · durable persona                      │
  └───────┬───────────────────────────────┬─────────────────────┘
          │                               │
  ┌───────┴────────────┐        ┌─────────┴────────────────────┐
  │  Native surfaces   │        │  FamiliarGateway  (ADR-0016) │
  │  Razor pages,      │        │  the Summoning Gate          │
  │  conversation,     │        └─────────┬────────────────────┘
  │  plan approval     │                  │
  └────────────────────┘        ┌─────────┴──────────┐
                                │                    │
                          MCP adapter          REST adapter
                                │                    │
                         ChatGPT, Claude       curl, future OpenAPI

  ┌─────────────────────────────────────────────────────────────┐
  │  Local workers — the do lane                                │
  │  Runner claims Started sessions; Claude adapter executes    │
  │  the ONLY thing that changes a repository                   │
  └─────────────────────────────────────────────────────────────┘
```

## What each boundary guarantees

### Demiplane owns truth

Task state, session state and project health are classified in one place. Nothing downstream forms a
second opinion — not the brief, not the gateway, not a model. The Familiar contradicting the Demiplane
about the same task is the specific failure this project exists to prevent (ADR-0011).

### Familiar context is where policy lives

Sensitivity is applied in the query, on both the entry and its project, so flagged rows are never in
memory beside a prompt being assembled. Superseded entries and raw provider prompts and output are
excluded. Retrieval has a relevance floor, below which the honest answer is "nothing" (ADR-0015).

Every consumer inherits all of it. That is why there is exactly one search.

### The gateway is a boundary, not a transport

`FamiliarGateway` decides what a body outside this server may see. MCP and REST are adapters over it
that hold no policy of their own and are interchangeable. No type under `Services/` is named after a
vendor.

It is read-only structurally: its dependencies cannot write, so exposing a mutation would mean adding
one.

### Workers are the only thing that changes anything

A session reaching a repository is started by a human approving a plan, claimed by a Runner on this
machine, and run with the tools its role allows. No external client can start one, and no model can
start one without a person pressing Approve (ADR-0012, ADR-0014).

## The invariant that survives every body

```
external conversation
    -> candidate memory or action
    -> Familiar validates it
    -> durable proposal
    -> human or policy gate
    -> canonical state change
```

Never `external LLM -> database`.

The reason does not weaken as a body gets further from the human's eye — it strengthens. A model whose
proposals a person reviews on the page where they were made is already one that should not write
directly. A model speaking through a vendor's client, with its own system prompt and its own opinions
about what the user meant, is further away rather than closer.

## Where to read further

| Concern | Document |
|---|---|
| Platform shape and lanes | [ADR-0001](decisions/ADR-0001-platform-architecture.md) |
| Provider-neutral worker bridge | [ADR-0006](decisions/ADR-0006-provider-neutral-runner-bridge.md) |
| Human-confirmed action | [ADR-0012](decisions/ADR-0012-conversational-reasoning-and-human-confirmed-action.md) |
| Talk lane / do lane split | [ADR-0013](decisions/ADR-0013-conversational-provider-split.md) |
| Conversational planning | [ADR-0014](decisions/ADR-0014-conversational-planning-and-plan-proposals.md) |
| Retrieval floor, repository snapshot | [ADR-0015](decisions/ADR-0015-retrieval-relevance-floor-and-repository-snapshot.md) |
| The Summoning Gate | [ADR-0016](decisions/ADR-0016-the-summoning-gate-and-interchangeable-bodies.md) |
| Connecting ChatGPT | [familiar-gateway-setup.md](familiar-gateway-setup.md) |
