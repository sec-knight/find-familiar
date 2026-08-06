# The Familiar

A conversation about one project, grounded in what is actually recorded.

`/Familiar/{projectId}` — reachable from that project's Demiplane.

---

## What it is for

The Demiplane states facts about a project. The Familiar lets you ask about them: *why is this
blocked*, *what did the Planner conclude*, *what should I do next*. It answers from the same
persisted rows the Demiplane renders, and it says when it cannot tell.

It is deliberately **not** autonomous. It answers, and it may propose one action — but nothing
happens until you click Confirm.

## What the page shows

1. **Project identity** — name, purpose, status, current context revision, links to the project and
   the Demiplane.
2. **Where things stand** — a short account of the project composed with no model at all, from the
   same snapshot a provider would receive.
3. **What I can't see** — the snapshot's own limitations, always visible, never behind a disclosure
   control. Limitations you have to click to read are limitations you don't know about.
4. **The conversation** — oldest first, each Familiar reply attributed to the provider and model that
   produced it.
5. **Proposed actions** — in their own bordered region outside the transcript, with Confirm and
   Dismiss.
6. **The input** — one textarea, up to 4,000 characters.

**The page is useful before the model says anything, and stays useful when it cannot.** Sections 1–3
never depend on a provider. With none configured the page still renders in full.

## What it can and cannot do

**It can:** answer from the project snapshot and the visible conversation; distinguish what is
recorded from what it infers from what it cannot tell; cite the tasks, sessions, handoffs and context
entries an answer rests on; propose at most one action per reply.

**It cannot:** run anything, change anything, or reach outside the snapshot it was given. It has no
tools — none are declared on any provider call, so there is no execution channel to abuse regardless
of what a reply says. It cannot approve a handoff, restart a worker, run a command, or touch a
repository.

**Model text is inert.** Replies render HTML-encoded with no markdown rendering and no autolinking. A
URL a model writes appears as characters; a command it writes appears as characters. Every link in
the conversation region was composed by the server from a persisted row.

## Proposed actions

Exactly two kinds:

| | What it does | Gates checked at confirmation |
|---|---|---|
| **Create a task** | One new task in this project, status Ready. No session starts, no worker notified. | Proposal still pending, token current, project active, **context revision unchanged since you reviewed it**, your edited title and outcome valid |
| **Start a Planner session** | One Started Planner session on a task that already exists here. An eligible worker may claim it automatically. | Proposal still pending, token current, project active, target task belongs to this project and still exists, no session already running on it |

For **Create a task**, the title and requested outcome are **editable before you confirm** — what
gets created is what you approved, not what the model wrote.

For **Start a Planner session**, the target is fixed and resolved server-side. There is no free-text
task id path; the model can only name a task that was in the snapshot it was shown.

**Why the revision gate differs.** Creating a task means approving *content* you read, so if the
project moved underneath you, what you read is no longer what you would be creating. Starting a
Planner means "run this role now", and the session reads whatever context is current when it starts —
so gating it on a revision would refuse a perfectly good session because an unrelated task changed.

**A proposal that can no longer be confirmed shows why** — "the project's context changed after this
was proposed", "that task already has a running session" — with Dismiss still available. Staleness is
worked out when the page renders, from current state. A proposal does not rot on a clock; it becomes
invalid when the world it described changes.

Every gate is re-checked **inside the transaction that would execute it**, not just when the page
rendered. All effects of a confirmation commit together or none do.

## Evidence

A reply may cite the rows it rests on. Those citations are checked against the exact snapshot that
produced the reply: an identifier that was not in it is **dropped silently**, with no note and no
complaint. A hallucinated citation is not an event worth reporting to you, and reporting it would
teach you to read model intent as system state.

Labels and links on surviving citations are composed by the server from the persisted row — never
from model prose.

## What is stored, and what is not

**Stored:** the visible text of each message, who wrote it, its order, the provider and model that
produced it, round-trip time, and any evidence that validated.

**Not stored:** prompts, thinking blocks, raw provider payloads, provider exception text. There is no
column for any of them, so nothing can write one. That is the guarantee — the absence of the column,
not a rule about using it.

## When it cannot answer

Every failure leaves the page fully usable with the deterministic account intact, and **your message
is saved before any provider is contacted** — a provider problem never costs you what you typed.

| What you see | What happened |
|---|---|
| No reasoning provider is configured, so I can only show you what is recorded. | None is set up. Everything above the conversation is still complete. |
| The reasoning provider could not be reached. Your message was saved and nothing was changed. | Endpoint down, wrong address, or a server error. |
| …rejected this application's credentials. That is a server configuration problem, not something you did. | Key missing, wrong, or out of credit. |
| …did not answer within *N* seconds. Your message was saved — try again. | Timed out. Common with a local model on slow hardware. |
| …is rate limiting this application right now. | Usually a shared free-tier pool. |
| …returned a response this application could not use. Nothing was changed. | The reply did not match the required shape. |
| …declined to answer that message. | A safety classifier refused. |
| This project is larger than I can summarise safely, so I did not send it. The summary above is complete and accurate. | The project exceeded the budget after every documented reduction. **Nothing was sent anywhere.** |

None of these names a host, a path, an exception, or a key. None speculates about a cause the server
did not observe. And none claims somebody else acted unless a competing decision genuinely exists.

## Bounds

| Bound | Value |
|---|---|
| Your message | 4,000 characters |
| Conversation history sent | 10 turns, trimmed further by measurement |
| Project snapshot | 24,000 characters, then refused rather than truncated |
| Whole request | 40,000 characters, re-measured immediately before sending |

The snapshot carries at most 20 tasks, 10 sessions, 10 pending handoffs, and 15 context entries
excerpted to 500 characters. **Every bound that actually bites produces a line in "What I can't
see"** — so an answer built on a partial view says so.

If a project does not fit, it is refused and the page tells you. A quietly truncated project would
answer about a different project than the one on your screen.

## Archived projects

Render fully and read-only. The input is disabled with the reason stated, and a crafted request is
refused server-side — a disabled control is a hint to a browser, not a boundary. Everything already
recorded stays readable; archiving hides nothing.

## Setting up a provider

See [`familiar-reasoning-setup.md`](familiar-reasoning-setup.md) — local models, hosted endpoints,
what each setting does, and how to tell whether a model will actually honour the reply schema.
