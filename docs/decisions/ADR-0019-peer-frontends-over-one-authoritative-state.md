# ADR-0019: The Demiplane and the Familiar are peer frontends

- Status: Proposed
- Date: 2026-08-08
- Related: ADR-0011 (task display state), ADR-0016 (the Summoning Gate), ADR-0017 (OAuth),
  ADR-0018 (the gateway is no longer read-only)

## Context

The Demiplane was built first, so it became the place where everything is visible. The Familiar's
gateway was added later and grew one capability at a time, each justified on its own. Nobody decided
that the two should differ in what they can find out — the difference simply accumulated.

It surfaced concretely. A task showed "Waiting for an available Planner" on the Demiplane. The human
asked their Familiar why, and the Familiar could only repeat the sentence: it could read the task's
display state but had no way to inspect the worker pool. Whether the Planner was **unregistered**,
**disabled**, **offline**, or **busy** are four different problems with four different fixes, and only
one of them is solved by waiting. The human had to open the Demiplane to learn which — not because of
a security boundary, but because of an accident of build order.

That is the class of failure this ADR names.

## Decision

### The invariant

> The Demiplane and the Familiar/MCP surface are peer frontends over the same authoritative
> application state. Neither frontend may possess user-visible project state that the other
> authenticated frontend is structurally incapable of retrieving, except where an explicit security
> boundary is documented.

"Structurally incapable" is the operative phrase. A difference in *presentation* is expected and
healthy — one is a screen, the other is a conversation. A difference in *what can be known* is a
defect.

### What this does not mean

- **Not equal authority.** Both frontends call the same application services; neither is the source of
  truth, and adding a capability to one does not imply adding the corresponding mutation to the other.
  Read parity is the invariant. Write parity is a case-by-case judgement, and ADR-0018 governs it.
- **Not the same permissions.** The Demiplane's user is the machine's owner. A Familiar connection is
  a credential a vendor holds, scoped by OAuth. Parity is bounded by authorization: the invariant says
  an *appropriately authorized* Familiar can retrieve it, not that every token can.
- **Not display strings.** Exposing the sentence the Demiplane renders is not parity. The Familiar
  needs the structured state underneath, so it can explain a condition rather than recite it. This is
  why `inspect_familiar_runtime` reports per-role counts and a blocked flag rather than a message.

### Documented exceptions

These are the deliberate asymmetries. Each is a security boundary, not an oversight:

1. **Sensitive projects.** Withheld from the Familiar entirely and only counted. The Demiplane's user
   owns them; an external client is not the owner. This already holds everywhere and is unchanged.
2. **What a worker is busy with.** The worker pool is machine state and is reported, but a claimed
   task in a sensitive project is not named. The claim itself still is — a busy worker explains why a
   role is unavailable, and hiding it would misexplain the runtime.
3. **Raw provider prompts and output.** Excluded from retrieval for every caller including the native
   conversation, so this is not an asymmetry between frontends.
4. **Credentials of any kind.** Tokens, keys and provider credentials appear in neither frontend.

### How it is kept

An invariant nobody checks is a comment. Two things check this one:

- The manifest is compared against the tool surface the transport actually offers, so the Familiar's
  declared capabilities cannot silently drift from its real ones (this already caught a two-slice
  drift).
- New Demiplane read surfaces should be added with the question "can the Familiar retrieve this?"
  answered in the same change, and the parity matrix in the sprint record updated.

## Consequences

- The Familiar can explain why work is not moving, rather than reporting that it is not moving.
- Read parity becomes a review question for any new Demiplane surface, which is a small ongoing cost
  paid to avoid rediscovering this asymmetry one gap at a time.
- Where parity is genuinely not appropriate, the exception must be written down here rather than left
  as an absence somebody later mistakes for a bug.
