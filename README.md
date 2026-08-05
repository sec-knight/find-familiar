# Find Familiar

> Preserve context between people, projects, and AI.

Find Familiar is an open-source orchestration platform for preserving durable project context, coordinating AI-assisted work, and keeping humans in control of what happens next.

Rather than treating conversations as the primary artifact, Find Familiar treats **projects, tasks, decisions, sessions, handoffs, and knowledge** as first-class citizens. AI providers are replaceable workers; the Familiar preserves continuity over time.

---

## Current Status

The current accepted baseline is **Sprint 10 — The Demiplane**, tagged `demiplane-baseline-v0`.

Find Familiar now includes:

- durable projects, tasks, sessions, context entries, and decisions;
- a provider-neutral Runner bridge;
- a Claude Code adapter;
- automatic worker pickup;
- human-gated Planner → Implementer → Reviewer handoffs;
- a per-project Demiplane showing health, pending decisions, provider readiness, work state, session chains, and plain-language explanations;
- migrations and tests sufficient to rebuild the development environment from an empty database.

**Sprint 11 is in progress:** the first real development cycle coordinated through Find Familiar itself.

This remains a development sandbox. Git history, migrations, ADRs, tests, and accepted documentation are the durable source of truth; the current SQLite data is disposable until real production use begins.

---

## Vision

Modern AI is capable, but every session begins with partial amnesia.

Humans repeatedly explain:

- project architecture;
- goals and constraints;
- prior decisions;
- current work;
- what succeeded or failed;
- what should happen next.

Find Familiar exists to remember those things so humans do not have to reconstruct them for every model and every session.

Its purpose is to preserve context across:

- AI sessions;
- projects;
- providers;
- devices;
- workers;
- time.

---

## Guiding Principles

- Context belongs to the user.
- AI providers are replaceable.
- Projects outlive conversations.
- Humans approve meaningful transitions.
- Small, well-defined tasks are easier to orchestrate.
- The Familiar should say only what its evidence supports.
- Unknown is better than a confident invention.
- Architecture and acceptance should be recorded durably.

---

## Architecture

```text
                         Human
                            │
              Browser / Mobile Web / CLI
                            │
                    Find Familiar Server
                            │
         ┌──────────────────┼──────────────────┐
         │                  │                  │
   Project Context     Task & Handoff      Demiplane
         │               Coordination       Projection
         │                  │                  │
         └──────────────────┼──────────────────┘
                            │
                  Provider-neutral Runner
                            │
              Worker + project-local mapping
                            │
          Claude Code • future adapters • local tools
```

The server owns durable project state and human decisions. Workers own machine-local execution details such as repository mappings, worktrees, installed runtimes, and provider credentials.

---

## Roadmap

### Completed foundation

- [x] Repository, ASP.NET Core server, SQLite persistence, migrations, and ADR process
- [x] Projects and durable project context
- [x] Tasks, context entries, and session history
- [x] Worker registration, leases, capabilities, and automatic pickup
- [x] Provider-neutral Runner bridge
- [x] Claude Code adapter
- [x] Conversational work intake
- [x] Human-gated session handoffs
- [x] Database-enforced single-started-session invariant
- [x] Demiplane v1 project command surface
- [x] Reproducible clean-database rebuild and accepted baseline

### In progress

- [ ] **First Familiar-managed development cycle**
  - create and explain real work inside Find Familiar;
  - run Planner, Implementer, and Reviewer through the existing workflow;
  - preserve the full chain in the Demiplane;
  - verify the experience on mobile.

### Planned: conversational Familiar

- [ ] Project-aware conversation grounded in durable Familiar state
- [ ] Ask “What should we work on?” and receive an evidence-backed recommendation
- [ ] Continue planning without re-explaining repository and sprint history
- [ ] Route broader architectural questions to external frontier models while preserving project context

### Planned: connected resource catalogue

- [ ] Catalogue AI runtimes, cloud connectors, tools, machines, repositories, worktrees, and services
- [ ] Distinguish server-provided resources from worker-provided resources
- [ ] Record resource instances, capabilities, ownership, and project requirements
- [ ] Resolve whether a resource is actually usable for a specific project

### Planned: operational awareness

- [ ] Worker and server heartbeats
- [ ] Resource states such as Ready, Degraded, Unavailable, Unknown, Misconfigured, and Offline
- [ ] Connected Resources view in the Demiplane
- [ ] Explain which tasks and projects are affected by an unavailable worker, connector, runtime, or machine
- [ ] Detect conditions such as an offline worker, missing runtime, hung provider process, or invalid project mapping

### Planned: guided setup and remediation

- [ ] Register connector and resource instances
- [ ] Test readiness without exposing secrets
- [ ] Create setup or remediation tasks from the Demiplane
- [ ] Separate discovery, registration, and machine-changing remediation permissions

### Future expansion

- [ ] Additional AI adapters and routing policies
- [ ] Build, test, commit, push, and review evidence as first-class project facts
- [ ] Voice, personality, and customizable Familiar presentation
- [ ] Home infrastructure, NAS, game-development, and household workflows
- [ ] Native mobile experience if mobile web no longer serves the need

---

## Development Philosophy

Find Familiar is being developed as a recoverable sandbox.

The repository is the save point:

- accepted work is merged to `main`;
- major usable baselines are tagged;
- migrations reconstruct the database;
- tests preserve lessons learned from discarded development state;
- ADRs preserve architectural reasoning;
- sprint acceptance records preserve what was reviewed and verified.

The current server may be rebuilt or replaced without becoming the sole source of truth.

---

## Architecture Decisions

Major architectural decisions are documented in [`docs/decisions`](docs/decisions) using Architecture Decision Records.

The project’s central idea remains:

> **Find Familiar exists to preserve context.**

The Demiplane adds a second essential rule:

> **The Familiar should never present an unsupported inference as fact.**

---

## Sprint Acceptance

Accepted sprints, commits, tags, test counts, and verification evidence are recorded in [`docs/sprint-acceptance.md`](docs/sprint-acceptance.md).

Current accepted code baseline:

- Sprint: **Sprint 10 — The Demiplane**
- Tag: `demiplane-baseline-v0`
- Accepted commit: `de035ee`
- Acceptance record on `main`: `8062144`

---

## License

License to be determined.
