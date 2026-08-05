# Conversational Familiar v0 — User Experience

What a person sees, in what order, and what every failure says to them.

The governing idea: **the page is useful before the model says anything, and it stays useful when the
model cannot.** Conversation is an addition to a project read-out, not a replacement for one.

---

## 1. Page structure

`/Familiar/{projectId}`

```
┌────────────────────────────────────────────────────────────────┐
│ Familiar · context revision 41                                 │  eyebrow
│ Find Familiar                                                  │  h1  (project name)
│ Preserve context between people, projects, and AI.             │  purpose
│ Project settings · Demiplane · All work                        │  links
├────────────────────────────────────────────────────────────────┤
│ WHERE THINGS STAND                                    (always) │
│ 7 tasks. 2 need your attention, 1 is running, 1 is blocked.    │
│ • "Windows worker setup" is waiting for your approval to       │
│   start a Reviewer session.                                    │
│ • "Cloudflare tunnel" is blocked: no enabled worker declares   │
│   Implementer.                                                 │
│                                                                │
│ What I can't see                                               │
│ • Showing 20 of 47 tasks, ordered by attention then recency.   │
│ • Provider remaining capacity is unknown.                      │
│ • Worker capabilities are self-reported and unverified.        │
├────────────────────────────────────────────────────────────────┤
│ CONVERSATION                                                   │
│                                                                │
│   You · 5 Aug, 14:02                                           │
│   why is the cloudflare task stuck?                            │
│                                                                │
│   Familiar · Claude (claude-opus-5) · 5 Aug, 14:02             │
│   It has a Started Implementer session that no worker has      │
│   claimed, and no enabled worker declares Implementer — so     │
│   nothing can pick it up, and nothing else can start on that   │
│   task while that session is open.                             │
│   Based on: task "Cloudflare tunnel" · session 3f2a…           │
│                                                                │
│ ┌────────────────────────────────────────────────────────────┐ │
│ │ ⚑ PROPOSED ACTION — nothing has happened yet               │ │
│ │                                                            │ │
│ │ Start a Planner session                                    │ │
│ │                                                            │ │
│ │ What will happen                                           │ │
│ │   One Planner session starts on "Cloudflare tunnel".       │ │
│ │   An eligible worker may claim it automatically.           │ │
│ │ Why                                                        │ │
│ │   You asked what would unblock this task.                  │ │
│ │ What will change                                           │ │
│ │   One new session row. The project's context revision      │ │
│ │   moves from 41 to 42. No file is touched by this action.  │ │
│ │                                                            │ │
│ │        [ Confirm ]   [ Dismiss ]                           │ │
│ └────────────────────────────────────────────────────────────┘ │
├────────────────────────────────────────────────────────────────┤
│ [ textarea — Ask about this project…            ] [ Send ]     │
│ 0 / 4000                                                       │
└────────────────────────────────────────────────────────────────┘
```

Rules that keep the shape honest:

- **The deterministic summary is above the conversation, not below it.** It is the thing most likely
  to be true and least likely to be missing.
- **"What I can't see" is always rendered**, never behind a disclosure control. Limitations that must
  be clicked to read are limitations users do not know about.
- **Proposed actions live in their own bordered region with their own heading**, outside the message
  bubble. A proposal must never be able to be mistaken for a sentence.
- **Every Familiar message is attributed** — provider and model, inline. When a project is later read
  by a different provider, the record says which one said what.
- **Evidence renders as links to real pages** (`/Tasks/Details/{id}`, `/Demiplane/{id}?taskId=…`), each
  composed by the server. No link in the conversation region ever originates from model text.
- **Model text is inert.** Encoded, `white-space: pre-wrap`, no markdown rendering, no autolinking.
  A URL the model writes appears as characters.

## 2. Message states

| Delivery | Rendering |
|---|---|
| `Delivered` | Normal. Attributed. |
| `Degraded` | Normal text plus a short note — reserved for a reply that arrived but whose action draft was rejected. Currently unused by design (a rejected draft produces no note); the state exists so a future partial outcome has somewhere honest to land. |
| `Failed` | No Familiar text at all. A `System` message carrying the §7 wording, styled as a system note, not as speech. |

The Familiar never speaks in an error's voice. "I couldn't reach my reasoning provider" is a
first-person claim about a component the Familiar does not observe; the page says it instead.

## 3. Failure copy

Verbatim, and the reviewer checks these against `FamiliarFailureWording`.

| Code | Copy |
|---|---|
| `provider-not-configured` | "No reasoning provider is configured, so I can only show you what is recorded. The summary above is complete." |
| `provider-unavailable` | "The reasoning provider could not be reached. Your message was saved and nothing was changed." |
| `provider-unauthenticated` | "The reasoning provider rejected this application's credentials. That is a server configuration problem, not something you did." |
| `provider-timeout` | "The reasoning provider did not answer within {n} seconds. Your message was saved — try again." |
| `provider-rate-limited` | "The reasoning provider is rate limiting this application right now. Your message was saved — try again shortly." |
| `provider-response-unusable` | "The reasoning provider returned a response this application could not use. Nothing was changed." |
| `provider-declined` | "The reasoning provider declined to answer that message." |
| `snapshot-too-large` | "This project is larger than I can summarise for the reasoning provider safely, so I did not send it. The summary above is complete and accurate." |

None of them names a host, a path, a header, an exception type, or a key. None of them speculates
about a cause the server did not observe.

## 4. Proposal states

| State | Rendering |
|---|---|
| Pending, valid | Full panel with Confirm and Dismiss. `CreateTask` shows title and requested outcome as **editable fields** — the human's edit is what gets created. |
| Pending, stale (revision moved, `CreateTask`) | Confirm replaced by: "The project's context changed after this was proposed, so I have not offered to create it. Ask again and I'll use what's current." plus Dismiss. |
| Pending, invalid (`StartPlanner`, task already running) | Confirm replaced by: "That task already has a running session, so another cannot begin." plus Dismiss. |
| Confirmed | Collapsed to one line stating what was created, with links to the task and session. |
| Dismissed | Collapsed to one line: "Dismissed. Nothing was created." |

Staleness is **derived at render time** from current state, never stored. A proposal does not rot on a
clock; it becomes invalid when the world it described changes, and the page says which part changed.

### Confirmation outcomes

After Confirm, a `TempData` status message, in the wording style `DecisionOutcomeWordingTests` already
governs:

| Outcome | Message |
|---|---|
| Created task | "Created the task "{title}". Nothing is running on it yet." |
| Started Planner | "Summoned a planner session on "{title}". A worker may claim it automatically." |
| Already decided | "That proposal was already decided, so nothing new was created." |
| Stale token | "This view was out of date, so nothing was changed. Review the current proposal and try again." |
| Task already running | "That task already has a running session, so another cannot begin. Nothing was started." |
| Project inactive | "This project is no longer active, so nothing was created." |
| Context moved | "The project's context changed after you reviewed this, so nothing was created. Ask again for a current proposal." |
| Database busy | "The database was busy and this was not applied. Nothing was created and nobody else confirmed it — try again." |
| Anything else | "This could not be completed, and nothing was changed." |

The last row claims no competing actor, for the reason ADR-0011 recorded: race wording is reserved for
outcomes that establish a real competitor.

## 5. Empty and edge states

- **No conversation yet** — the conversation region shows one line: "Nothing asked yet. Ask about this
  project, and I'll answer from what's recorded." The input is present and enabled.
- **Empty or whitespace message** — client-side `required` plus server-side validation error next to
  the textarea. No row is written.
- **Over 4 000 characters** — the counter turns into an error state, Send is rejected server-side with
  "Keep your message to 4,000 characters or fewer."
- **Archived project** — the page renders fully and read-only. The input is disabled with "This
  project is archived, so no work can be started from here." The summary and history remain readable;
  archiving hides nothing.
- **Project with no tasks** — the summary says "No tasks yet." and nothing more. It does not
  editorialise about what should exist.

## 6. Accessibility and progressive enhancement

- Server-rendered forms; no JavaScript is required for any function on this page. The character
  counter is the only scripted element and its absence changes nothing.
- Status messages use `role="status"`; validation errors are associated with their control.
- Every state marker is paired with text — the same rule the Demiplane's `MarkerFor` follows. Colour is
  never the only signal.
- The proposed-action region is a `<section>` with an `aria-labelledby` heading, so it is reachable and
  announced as a distinct landmark rather than as part of the transcript.
- Confirm and Dismiss are real `<button type="submit">` elements in a form with an antiforgery token,
  not links. Nothing state-changing is reachable by `GET`.

## 7. Copy voice

Short declaratives. No exclamation marks, no apologies, no emoji. The Familiar says what is recorded,
what it infers, and what it cannot tell, and it uses those three registers visibly:

> **Recorded** — "The Implementer session started at 14:02 and no worker has claimed it."
> **Inferred** — "That suggests nothing can pick it up, because no enabled worker declares Implementer."
> **Unknown** — "I can't tell you why the earlier session was cancelled; the reason wasn't recorded."

The failure of this page would be a Familiar that sounds confident about the third case. Every review
of this feature should read a transcript looking for exactly that.
