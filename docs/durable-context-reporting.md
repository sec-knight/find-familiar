# Reporting durable context

How an agent, worker, or operator records something worth keeping into Find Familiar.

**The rule:**

> The agent reports facts. Find Familiar validates and records them.
> The agent never writes Find Familiar's database directly.

---

## Why this exists

During Sprint 14/15 work, useful context was written by opening the SQLite file and inserting rows.
The records were repaired and integrity checks passed, but the attempt failed first, and instructively:
a project id supplied in lowercase where the database stores uppercase matched nothing, SQLite's
foreign keys were not enforcing, so three rows inserted successfully **belonging to no project**, and
the project's context revision never moved.

Every one of those is an invariant the application already knew and the raw write did not. That is the
general case, not an unlucky one: a direct write cannot enforce rules it is bypassing.

## Never work around a denied mutation by switching tools

During the same work, a database mutation refused through one tool was then performed through another.
That must not be a normal operating pattern.

**A denied write effect remains denied regardless of execution mechanism.** The refusal attaches to the
effect on the system, not to the command that expressed it. If a write is blocked, the correct
responses are to stop and report, or to ask the human — never to reach the same effect through a
different program, language, or shell.

## The supported path

`IProjectContextRecordingService.RecordAsync` is the one implementation of the rules. The Demiplane's
project page calls it, and so does the trusted machine-local route below. It owns project lookup and
identifier normalisation, validation, the transaction, entry creation, the context-revision increment,
foreign-key correctness, provenance, and deterministic typed failures.

For a process on this machine or the tailnet, the route is:

```bash
curl -s -X POST "http://127.0.0.1:5199/api/context/projects/<project-id>/entries" \
  -H "Authorization: Bearer $FAMILIAR_RUNNER_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
        "kind": "Implementation",
        "title": "Slice 0 shipped: sessions are told which workspace they may reach",
        "content": "…",
        "provenance": "RepositoryVerified",
        "recordedBy": "claude-session"
      }'
```

It is behind the **runner bridge credential** — the same machine-to-machine token the Runner and the
snapshot hook already use. Concretely: it is **not** part of the Summoning Gate. It is not published
through Tailscale Funnel, ChatGPT cannot reach it, no OAuth scope grants it, and it has nothing to do
with `familiar.decide`.

| Response | Meaning |
|---|---|
| `200` | Recorded. Returns `contextEntryId` and the new `contextRevision`. |
| `400` | Validation failed, or an unusable category/provenance. Nothing written. |
| `404` | No project has that id. Nothing written. |
| `409` | Project inactive, or `expectedContextRevision` was stale. Nothing written. |
| `503` | Database busy. Nothing written; retry. |

### Provenance is required

How well a fact is known is part of the fact. A reader who cannot tell a verified claim from a
reported one will treat both as settled.

| Value | Use when |
|---|---|
| `RepositoryVerified` | You checked the repository, database, or a test run. |
| `SessionReported` | An agent session produced it. |
| `HumanReported` | The human stated it; not verifiable from here. |
| `ExternalReported` | An external tool or client asserted it, unverified. |

`Unspecified` exists only for rows written before provenance did. The service refuses it on a new
entry.

### The optional revision fence

Supply `expectedContextRevision` when your entry only makes sense against the view you read; omit it
when reporting an independent observation. A stale value is refused with `409` rather than applied.

## If no supported path is available

A session may not be able to reach the route — no credential, no network, a policy that forbids it.
**Do not fall back to writing the database.** Return a structured artifact instead, and let a human or
a later process record it:

```
DURABLE CONTEXT CANDIDATE

Project:      Find Familiar (2f098ffc-b981-4b1a-b47d-f3a6b93e1771)
Category:     Implementation
Title:        <one line>
Provenance:   RepositoryVerified | SessionReported | HumanReported | ExternalReported
Commits:      <relevant commit hashes, if any>
Timestamp:    <UTC, where it matters>

Body:
<concise body — what happened, why it matters, what a future reader needs>

Not recorded: no supported application-level context-write path was available to this session.
```

That last line is not optional. A candidate that does not say it was never recorded will eventually be
read as though it was.

## What this is not

Recording context creates exactly one entry against one project. It cannot create or change a task,
start a session, approve anything, edit or delete an existing entry, or touch any other table. It is
not generic write access, and there is no parameter that makes it one.
