# ADR-0013: Separate conversational provider from the execution runner

- Status: Proposed
- Date: 2026-08-06
- Supersedes: none
- Related: ADR-0012 (drop native Anthropic project)

## Context

Sprint 11 delivered the Runner bridge and the Claude Code adapter. Both are built
for *agentic execution*: spawn a session, do work against a repository, return.

The Familiar conversation described in the roadmap has different requirements:

- first token in under a second, streamed;
- dozens of turns per sitting;
- no filesystem or repository access;
- cheap enough that the human never weighs whether to ask;
- resumable across devices.

Attempting to serve conversation through the existing Runner would mean a process
spawn per turn, no token-level streaming into the browser, and a session abstraction
that carries repository mapping concerns a conversation does not have.

## Decision

Introduce a second provider seam, `IFamiliarChatProvider`, independent of the Runner.

```
                        Human
                          |
                  Familiar Conversation
                          |
              +-----------+-----------+
              |                       |
         TALK lane                 DO lane
      IFamiliarChatProvider     Runner + Worker
      streaming, stateless      agentic, repo-local
              |                       |
        xAI / OpenRouter        Claude Code adapter
              |                       |
              +-----------+-----------+
                          |
                Durable Familiar state
```

The talk lane is stateless with respect to the provider. The server owns all
conversation state and sends the full assembled context on every call. No provider
feature that stores conversation state server-side is used.

### Initial provider

xAI Grok, model `grok-4.1-fast`, via the OpenAI-compatible endpoint.

- Rates at time of writing: $0.20 / $1M input, $0.50 / $1M output.
- 2M context window, which allows slices 1-3 to proceed without a retrieval layer.
- Estimated ~$0.003 per grounded turn at ~12k input / ~600 output.

### Account posture

- Dedicated xAI team, Zero Data Retention enabled at team level.
- The team is **not** enrolled in the data-sharing programme. That enrolment is
  irreversible and grants training rights over all traffic; the credit it returns is
  worth an order of magnitude less than the cost of the traffic this project sends.
- ZDR disables the stateful Responses API, Files, Collections, and Batch. This project
  uses none of them, because the server owns conversation state by design.

### Configuration

Provider, model, and account identity travel as one unit so a provider swap cannot
silently inherit the wrong credential:

```
Familiar__Chat__Provider = xai
Familiar__Chat__Model    = grok-4.1-fast
Familiar__Chat__Team     = familiar-prod    # label only, for operator clarity
XAI__ApiKey              = <secret, via EnvironmentFile, 0600, never in the repo>
```

Model IDs are configuration, never compile-time constants. Provider model rosters
churn; a retired model must surface as a visible error in the UI, not a dead stream.

## Consequences

### Positive

- Conversation latency is decoupled from session-spawn cost.
- The Runner and its Claude Code adapter are untouched by Sprint 12.
- Provider swap is a configuration change.
- ZDR closes the de-identified-data derivation clause in the standard terms at no
  functional cost to this architecture.

### Negative

- Two provider seams to maintain rather than one.
- The talk lane's tool-calling reliability is provider-dependent and not yet proven
  on the chosen model.

### Neutral

- The second implementation of `IFamiliarChatProvider` should be Anthropic-shaped
  rather than another OpenAI-compatible endpoint. xAI and OpenRouter share a request
  shape; implementing both proves nothing about the abstraction. Anthropic's differs
  enough to prove it.

## Constraint carried forward

The talk lane must never write to project state. Model output produces prose and
*proposals*; proposals are durable records; a human approves; execution runs through
`FamiliarActionService` with gates re-checked inside the executing transaction.

Exactly one non-comment reference to `IWorkflowDispatchService` under
`Services/Familiar` remains the invariant. Sprint 12 is read-only and does not
approach it.
