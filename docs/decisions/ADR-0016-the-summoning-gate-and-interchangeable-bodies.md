# ADR-0016: The Summoning Gate, and interchangeable bodies

- Status: Proposed
- Date: 2026-08-07
- Supersedes: none
- Related: ADR-0006 (provider-neutral runner bridge), ADR-0012 (human-confirmed action),
  ADR-0013 (talk lane / do lane split), ADR-0014 (conversational planning), ADR-0015 (retrieval floor
  and repository snapshot)

## Context

Find Familiar has been built as an application with a Familiar page inside it. That framing has
reached its limit.

The durable thing this project produces is not a chat UI. It is continuity: the projects, decisions,
sessions, recorded context, sensitivity rules and identity that survive a conversation ending, a
device changing, and a model provider being replaced. The chat UI is one way to reach that, and it is
not the way the user actually wants to reach it most of the time — the phone in their pocket already
runs a frontier client with better speech, better latency and better ergonomics than this server will
ever have.

**The Familiar is the persistent spirit; a frontier client is one body it can inhabit.** ChatGPT
today; Claude, Grok, a local model, or the native pages tomorrow. Changing bodies must not cost the
Familiar its memory.

The immediate target is ChatGPT on the user's Pro plan, which can connect custom MCP servers in
Developer Mode with read and fetch permissions. Write-back through MCP is not currently available on
that plan.

That constraint should shape *which slice ships first*. It must not shape the architecture, because
the constraint belongs to one vendor at one moment and the architecture has to outlive both.

## Decision

### 1. A provider-neutral gateway, with transports as adapters over it

`FamiliarGateway` is an application boundary. It answers four questions — who is this Familiar, what
does it remember about X, which projects exist, what is the state of project Y — and it decides,
once, what a body outside this server may see.

```
                        Find Familiar
                             |
                     FamiliarGateway          <- identity, retrieval, sensitivity, bounds
                             |
              +--------------+--------------+
              |                             |
        MCP adapter                   REST adapter
      (ChatGPT, Claude)          (curl, future OpenAPI)
```

**Both adapters ship in this sprint, deliberately.** A boundary with exactly one consumer is an
assertion rather than a demonstration. Two adapters serialising the same contracts, sharing one
authentication filter, and holding no policy of their own is the cheapest available proof that
nothing about a specific vendor leaked into the domain. The REST surface is also what makes the
gateway verifiable with `curl` before any decision about exposing anything to the internet.

**No type in the domain is named after a vendor.** The word ChatGPT appears in setup documentation
and nowhere in `Services/`. `FamiliarMcpTools` is in `Api/`, where transports live.

### 2. The gateway reads the mind-map; it does not rediscover reality

Every answer comes from `IFamiliarContextRetrievalService` and `IFamiliarStandingBriefService` — the
same two services the native conversation is built on.

This is the load-bearing decision, and it is what makes the security properties free rather than
duplicated. Sensitivity on both entry and project, exclusion of superseded entries, exclusion of the
`Prompt` and `RawOutput` kinds, the relevance floor and its no-match disclosure: all of it is enforced
in the query, once, for every caller. A gateway that ran its own search would need its own copy of
each rule, the copies would drift, and the drift would surface to an external client — the worst place
to find it.

The same rule settles repository awareness. ADR-0015's snapshot is an ordinary context entry, so an
external body learns the branch, head and tracked paths through normal retrieval. **The gateway does
not shell out to git, walk the filesystem, or scan anything.** A second definition of what is real is
worse than a stale first one.

**Rejected: a dedicated external-facing search.** Tempting, because an external body has different
needs from a prompt — bigger excerpts, more items, its own ranking. Rejected because every one of
those knobs is a place where the two search paths could disagree about what this system knows, and
because the first divergence would be discovered by a frontier model confidently reporting something
the native Familiar would have refused to say.

### 3. Read-only, structurally rather than by convention

`FamiliarGateway` depends on two read-only services and on nothing that can write — no
`IWorkflowDispatchService`, no plan drafting or approval service, no `DbContext`. Exposing a mutation
would require adding a dependency, which is a visible act in review rather than an omission.

The MCP surface is four tools, all annotated `readOnly` and `destructive: false` in the protocol, so a
client can see the guarantee rather than infer it from the names. There is no `get_everything`: a tool
that returns the store spends a context window on a question nobody asked and removes the one thing
that makes retrieval trustworthy, which is that something decided what was responsive and can say why.

**Write-back, when it comes, takes this shape:**

```
external conversation
    -> candidate memory or action
    -> Familiar validates it
    -> durable proposal row
    -> human or policy gate
    -> canonical state change
```

and never:

```
external LLM -> database
```

This is the same rule ADR-0012 and ADR-0014 established for the native conversation, and the reason
does not weaken with distance — it strengthens. A model whose proposals a person reviews on the page
where they were made is already a model that should not write directly; one speaking through a
vendor's client, with its own system prompt, its own tools, and its own opinions about what the user
meant, is further from the human's eye rather than closer.

### 4. A bearer token, not OAuth

The gateway authenticates with a bearer token from configuration, compared in fixed time over SHA-256
digests, failing closed when unset.

**Rejected: OAuth**, which the MCP SDK supports. It would mean an authorization server, a redirect
surface and a token store — for one user, with one client, granting all-or-nothing access to a
read-only surface. More moving parts to get wrong, protecting the same thing. This reverses the moment
there is a second person, a second concurrent client, or any need to grant partial access.

**A separate credential from the runner bridge.** They look alike and are not: the runner token is
handed to a process on this machine or the user's tailnet; this one is handed to a frontier vendor's
servers and crosses the public internet on every call. One credential across two trust domains means
a leak at either end gives away both, and makes rotating one impossible without breaking the other.

**Two settings, not one.** `Enabled` and `Token` are separate so a deployment can state "no external
body may reach this" as a fact rather than as the absence of a secret. With `Enabled` false the routes
are not mapped at all: a prober gets 404, which says nothing, rather than 401, which confirms there is
a Familiar here behind a credential worth guessing at.

### 5. Identity is configuration, for now

`FamiliarIdentityOptions` holds a name, a description, and an optional compact note on register.

Configuration rather than a table, for proportion rather than principle: the first slice needs three
strings, and a migration plus an editor page to hold three strings is work spent at the wrong end of
the problem. **This becomes a row the moment identity acquires anything a person would want to edit
without a deploy, or anything that differs per body.**

`Guidance` is bounded at 500 characters on purpose. The Familiar's character is enforced by this
server on the paths this server owns; shipping a paragraph of system prompt to an external client
would hand that character to whichever body connected, to keep or discard as it liked.

## Consequences

- An external frontier client can answer from this user's real project memory without the user pasting
  anything, and without that client being able to change anything.
- The security properties an external caller inherits are the ones the native path already had. New
  sensitivity rules apply everywhere at once, because there is one place they live.
- The gateway is one more consumer of retrieval. A change to retrieval's contract is now felt in three
  places rather than two — the reason `restrictToProject` was added as an opt-in parameter defaulting
  to current behaviour rather than as a change to what `focusProjectId` means.
- Two credentials to manage instead of one, and two to rotate.
- ChatGPT decides when to call these tools. Tool descriptions can encourage selective use and cannot
  compel it; the same Familiar and the same context will produce different answers through different
  bodies, and that is inherent to the design rather than a defect in it.
- The MCP surface adds a dependency on `ModelContextProtocol.AspNetCore` and on a protocol still
  moving. It is confined to `Api/Gateway/`; a breaking change there costs one adapter, not the domain.

## What would reverse this

- **A second user, or granular permissions.** The bearer token becomes OAuth, and identity becomes a
  row with per-body scope.
- **Retrieval outgrowing keyword overlap.** ADR-0015 already names the trigger. The gateway inherits
  whatever replaces it without changing shape, which is the point of it not having its own search.
- **Write-back arriving.** That is a proposal table, a review surface and a policy gate — an ADR of
  its own, not an extension of this one.
