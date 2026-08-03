# ADR-0002: Canonical Data and Rendered Context Projections

**Status:** Accepted

**Date:** 2026-07-31

---

## Context

Find Familiar must preserve durable project context across people, tasks, and isolated AI sessions. If a Markdown handoff document, a browser page, or an AI-provider conversation becomes authoritative, the same knowledge will be duplicated and eventually diverge.

The platform needs one durable representation of project knowledge that can serve human-facing pages, AI-session context, and future worker or API consumers.

---

## Decision

Find Familiar will store its durable domain data in a relational database. SQLite is the initial persistence implementation.

Projects, tasks, agent-session records, and context entries are canonical records. A context entry records durable knowledge such as a goal, constraint, decision, plan, implementation handoff, or review result. Agent sessions provide provenance and lifecycle information; raw conversation transcripts are not the primary persistence model.

The application will create a shared context projection from canonical records. The Razor Pages UI, Markdown context builder, and JSON output are renderers of that projection.

```text
SQLite canonical records
        ↓
Context projection service
   ├─ HTML / Razor Pages
   ├─ Markdown / AI handoff
   └─ JSON / workers and API
```

Rendered Markdown is not stored as the authoritative project context. If a rendered snapshot is retained later for audit or caching, it must be labeled as derived and record the source context revision or hash.

---

## Consequences

### Positive

- A new AI session can reconstruct relevant context without replaying prior chats.
- Human, Markdown, and JSON consumers share the same facts and selection rules.
- AI providers remain interchangeable.
- Context can be corrected through explicit durable records rather than hidden prompt edits.
- The initial system remains small: a relational model and deterministic queries are sufficient.

### Negative

- The context-selection and rendering rules must be designed and tested deliberately.
- Free-form session output must be converted into explicit context entries before it becomes durable knowledge.
- A renderer can become misleading if it bypasses the shared context projection.

---

## Alternatives Considered

### Markdown files as canonical context

Rejected because different Markdown documents would become competing sources of truth and are difficult to query, validate, or render consistently for other consumers.

### Conversation transcript as canonical context

Rejected because conversations are temporary, provider-specific, and contain more material than future sessions need.

### Renderer-specific database queries

Rejected because HTML, Markdown, and JSON would slowly acquire different context-selection rules.

---

## Initial Scope

The first vertical slice uses SQLite and Razor Pages only. It deliberately excludes embeddings, vector databases, knowledge graphs, autonomous agents, authentication, cloud infrastructure, and provider integrations.

The proof of the decision is a Planner, Implementer, and Reviewer using three independent sessions. Each receives generated Markdown context and records durable results as context entries for the next session.

---

## Clarification (2026-08-02): Bounded raw output

A `RawOutput` context entry may retain one bounded provider response for a session. It is supplemental evidence of what a session actually produced, not the primary persistence model. Durable knowledge for the next session still needs explicit structured context entries — `Summary`, `Decision`, `Plan`, `Implementation`, `Review`, and so on. `RawOutput` does not authorize an accumulating conversation transcript.
