# ADR-0018: The gateway is no longer read-only

- Status: Proposed
- Date: 2026-08-08
- Amends: ADR-0016 (the Summoning Gate), whose "read-only in every direction, structurally" is now
  true of everything except one operation
- Related: ADR-0017 (OAuth for the Summoning Gate, and its scope-split amendment), ADR-0009
  (human-gated role handoff), ADR-0012 (human-confirmed action)

## Context

ADR-0016 built the Summoning Gate read-only and said so as a structural fact rather than a promise:
the gateway held no service that could write, so exposing a mutation would take adding a dependency,
which is a visible act in a review rather than an oversight.

That act has now been taken, deliberately and once. This ADR is the record of it, because an
architecture note that says "read-only in every direction" and is no longer true is worse than no
note at all — a future reader would trust it.

The pressure that justified it: the human could ask the Familiar what needed them and be told
accurately, and then had to open the Demiplane to answer. The loop was open at exactly the point
where the person's own judgement was required.

## Decision

### 0. Amendment: two decision kinds, one relay

Plan proposals joined session handoffs as a relayable decision. The shape is unchanged — the relay
still carries only a decision id, the concurrency token it was read with, and approve or decline — and
the kind is read from the row rather than chosen by the caller, so a client cannot select which gate
its decision reaches.

One thing is specific to plans and worth stating. A plan is a list of tasks a person may edit before
agreeing to it, so the risk is not that a model approves the wrong plan but that it approves a
*different* plan from the one the person read. The relay therefore sends no item decisions at all,
which the approval service reads as "the human changed nothing": every item keeps the inclusion and
the wording they were shown. Editing a plan stays where the editing controls are.

### 1. One write, and it is a relay

`submit_familiar_decision` carries a decision the human has explicitly made to a gate Find Familiar
itself raised. It takes a decision id, the concurrency token the decision was read with, and a choice
of `approve` or `decline`. There is no note, no reason, and no free-text field a model could fill with
words the person never said.

It is not a general write capability. It cannot create a task, start arbitrary work, edit a record,
write a memory, or take any decision the workflow has not already surfaced as awaiting a human.

### 2. The client is a courier, not an authority

Legality is re-decided inside `ISessionHandoffApprovalService` — the same transaction the Demiplane's
own button posts to, with the same conditional consume, the same token fence, and the same partial
unique index behind it. This gateway does not evaluate whether a decision is allowed; it asks. A
fabricated decision id reaches a service that refuses it, never a table.

### 3. The read gateway stays provably read-only

The write lives in `IFamiliarDecisionGateway`, not on `IFamiliarGateway`. ADR-0016's claim about that
type is worth more than the convenience of one more method on it, so it still holds no service that
can write, and the single mutation is visible in a type name.

### 4. Visibility is checked before delegation

`ISessionHandoffApprovalService` knows nothing about sensitivity, because the Demiplane's user owns
every project and an external client does not. A submission is therefore matched against the decisions
the caller may actually read, and a decision in a sensitive project answers exactly as one that does
not exist.

### 5. The manifest declares capabilities by allowlist

`familiar_manifest` names four reads and one write, written out rather than reflected over the
registered tools. Reflection would mean adding a method silently makes the Familiar claim a new public
capability. The cost of the allowlist is that it can go stale — it did, by two capabilities and one
whole category, between Sprint 14 and this slice — so a test now compares the declaration against the
tool surface the transport actually offers.

## Consequences

- The human can manage the Familiar from a conversation without opening the Demiplane, for the one
  decision type the workflow currently raises.
- ADR-0016's read-only claim is now bounded rather than absolute, and this ADR is where a reader
  learns that.
- Adding a second write is a decision somebody must make explicitly: a new scope check, a new
  allowlist entry, and a new ADR amendment, none of which happen by editing an array.
- The Demiplane remains authoritative and unchanged. Nothing about the human gate moved; only the
  surface a person can reach it through.
