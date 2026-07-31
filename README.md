# Find Familiar

> Preserve context between people, projects, and AI.

Find Familiar is an open-source orchestration platform that helps individuals and teams maintain long-term context across projects, conversations, and AI providers.

Rather than treating conversations as the primary artifact, Find Familiar treats **projects, tasks, decisions, and knowledge** as first-class citizens. AI models become interchangeable tools that assist in accomplishing work while the Familiar preserves continuity over time.

---

## Vision

Modern AI is incredibly capable, but every session begins with partial amnesia.

Humans repeatedly explain:

- Project architecture
- Goals
- Preferences
- Decisions
- Prior work

Find Familiar exists to remember those things so humans don't have to.

Its purpose is to preserve context across:

- AI sessions
- Projects
- Devices
- Time

---

## Guiding Principles

- Context belongs to the user.
- AI providers are replaceable.
- Projects outlive conversations.
- Small, well-defined tasks are easier to orchestrate.
- Architecture should be documented through ADRs.

---

## Planned Architecture

```
                  Human
                     │
      ┌──────────────┼──────────────┐
      │              │              │
   Browser       Mobile App        CLI
      │              │              │
      └──────────────┼──────────────┘
                     │
              Find Familiar API
                     │
          ┌──────────┴──────────┐
          │                     │
      Task Engine          Memory Engine
          │                     │
      Worker Pool       Knowledge Store
          │
   Claude • Codex • OpenAI • Local Models
```

---

## Initial Roadmap

### Milestone 0 — Foundation ✅

- Git repository
- ASP.NET Core server
- Docker
- ADR process

### Milestone 1

- Dashboard
- Health endpoint
- Branding
- Version information

### Milestone 2

- Projects

### Milestone 3

- Tasks

### Milestone 4

- Workers

### Milestone 5

- AI provider integrations

---

## Architecture Decisions

Major architectural decisions are documented in the `docs/decisions` directory using Architecture Decision Records (ADRs).

The first ADR establishes the project's core philosophy:

**Find Familiar exists to preserve context.**

---

## License

License to be determined.
