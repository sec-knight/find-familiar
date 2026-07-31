# ADR-0001: Find Familiar Exists to Preserve Context

**Status:** Accepted

**Date:** 2026-07-31

---

## Context

Modern AI systems are extraordinarily capable, but every conversation begins with limited context. Users repeatedly explain the same projects, goals, preferences, architecture, and decisions across sessions and across different AI providers.

This repetition wastes time, consumes tokens, and places the burden of remembering on the human instead of the software.

Find Familiar exists to reverse that relationship.

The Familiar should become the long-term steward of context while AI models become interchangeable reasoning and execution engines.

---

## Decision

Find Familiar will be designed as a personal orchestration platform.

Its primary responsibility is preserving context over time.

The platform owns:

- Project knowledge
- User preferences
- Long-term memory
- Task orchestration
- Integrations with external systems

Artificial intelligence providers are treated as replaceable capabilities rather than the center of the system.

The web interface, mobile application, voice interface, command line tools, and future clients are all equal consumers of the same backend services.

---

## Consequences

### Positive

- Memory survives individual AI sessions.
- AI providers can be replaced without redesigning the system.
- Multiple AI systems can collaborate on the same project.
- Every interface shares the same source of truth.
- Personal knowledge remains under user control.

### Negative

- The orchestration layer becomes more complex.
- More infrastructure is required than a single chatbot.
- Context management becomes a core engineering responsibility.

---

## Alternatives Considered

### Provider-specific assistant

Rejected because it couples long-term knowledge to a single AI vendor.

### Chat-first architecture

Rejected because conversations are temporary while projects are long-lived.

### AI wrapper

Rejected because Find Familiar should own orchestration rather than simply forwarding prompts.

---

## Future Considerations

Possible future capabilities include:

- Multi-agent task execution
- Voice interaction
- Local and cloud AI providers
- Smart home integrations
- Knowledge graph construction
- Family and household knowledge sharing
- Long-term archival of project history

---

## Guiding Principle

> Preserve context.
>
> AI providers are tools.
>
> The Familiar is the keeper of knowledge.
