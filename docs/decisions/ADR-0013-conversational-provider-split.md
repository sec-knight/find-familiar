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

xAI Grok, via the OpenAI-compatible endpoint.

Model: `grok-4.20-0309-non-reasoning`, 1M context, which allows slices 1-3 to proceed
without a retrieval layer.

Non-reasoning deliberately. A reasoning model bills its thinking as completion tokens and
delays the first visible one; both are the opposite of what a conversation wants, and the
reasoning variant costs the same, so there is no trade being made.

Cached input is priced ~6x below fresh input. That is what the stable-to-volatile prompt
ordering in the sprint plan earns, and it turns a structural tidiness argument into a
recurring cost saving.

**`grok-4.1-fast`, this ADR's original choice, no longer exists.** It was retired between
writing this and shipping slice 2, and the endpoint answers a request naming it with an
error. That is not a footnote — it is the first prediction in this document to be tested,
and the mechanism it argued for is what caught it: the model id was configuration, the
failure surfaced as a classified error on the page with provider attribution recorded, and
the fix was one line in an EnvironmentFile with no code change and no redeploy.

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
Familiar__Chat__Provider        = xai
Familiar__Chat__BaseAddress     = https://api.x.ai/v1
Familiar__Chat__Model           = grok-4.20-0309-non-reasoning
Familiar__Chat__Team            = FindFamiliar-Prod   # label only, never sent
Familiar__Chat__ApiKeyVariable  = XAI_API_KEY         # the NAME of the variable
XAI_API_KEY                     = <secret, via EnvironmentFile, 0600, never in the repo>
```

**The credential is `XAI_API_KEY`, not `XAI__ApiKey` as first written here.** A double
underscore is ASP.NET's configuration separator, so that spelling would have bound the
secret into `IConfiguration`, where a configuration dump can print it. Configuration holds
the *name* of the environment variable; the value is read from the environment directly and
never travels through configuration at all. This matches what `Familiar:Reasoning`
already does, and the consistency is the point — one rule about secrets, not two.

Model IDs are configuration, never compile-time constants. Provider model rosters churn; a
retired model must surface as a visible error in the UI, not a dead stream. This was
vindicated within days: see the note on `grok-4.1-fast` above.

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
- Status classification is coarser than hoped. xAI answers a rejected credential with
  HTTP 400 rather than 401, so a bad key and a retired model land on the same status. The
  response body would distinguish them and is deliberately never read, because error bodies
  echo the request and can name a host, an account or part of a key. The wording therefore
  names both causes and claims neither — an earlier version guessed the likelier one and
  was wrong the first time it mattered, on a surface whose entire premise is not doing
  that.

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
