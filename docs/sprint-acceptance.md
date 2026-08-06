# Sprint acceptance record

A durable record of which sprints have been accepted, what was accepted, and what was verified at the
time. Git history, migrations, tests and ADRs remain the source of truth; this file is the index that
says which point in that history was signed off, and on what evidence.

---

## Sprint 10 — The Demiplane

**Accepted 2026-08-05.**

| | |
|---|---|
| Accepted commit | `de035ee770bfa48a700cbef8b4a95b0277a0e5d4` — *Say only what the data supports, and classify locks on every path* |
| Review range | `6f6e41d..de035ee` (5 commits) |
| Merge into `main` | Fast-forward from `fc16678`. The repository has no merge commits; history stays linear, so there is no merge commit to cite — final `main` head **is** the accepted commit. |
| Final `main` head at acceptance | `de035ee770bfa48a700cbef8b4a95b0277a0e5d4`. `main` advances past the tag by this documentation-only commit; the accepted code baseline remains `de035ee`. |
| Tag | `demiplane-baseline-v0` (annotated) → `de035ee` |
| Tests | **523 passed, 0 failed, 0 skipped** — three consecutive full runs on the branch, plus a fourth from merged `main` |
| Final review result | **PASS** — no blocking findings |

### What was verified

- `dotnet build` — succeeded, 0 warnings, 0 errors.
- `dotnet test` — 523/523, four times (three on the branch, once from merged `main`).
- `dotnet ef migrations has-pending-model-changes` — no model changes since the last migration.
- `git diff --check 6f6e41d HEAD` — clean, no whitespace errors.
- Secret scan and machine-path scan of the range — no credentials and no machine-local paths. The
  only matches are synthetic fixtures in `ProviderCapacityServiceTests`, which exist precisely to
  assert a reader's exception text cannot put a token or a local path on screen.
- Merge topology — strictly linear; every Sprint 05–10 commit present in order, Sprint 09's
  `20260805011808_SessionHandoffsAndStartedSessionUniqueness` migration included, no accepted commit
  stranded on another branch, no unrelated experimental commits.

### What the sprint established

The Demiplane is a per-project command surface that states only what the persisted data supports.
The rules behind it are recorded in [ADR-0011](decisions/ADR-0011-task-display-state-and-provider-capacity.md):

- Display state is derived in one place and is never execution authority.
- Absence of a handoff row is never reported as a human decision — `NoNextStepProposed` and
  `ProposedStepDeclined` are distinct, and decisions stay scoped to the latest terminal session's
  `SourceSessionId`.
- Failure categories come only from the fixed diagnostic strings this codebase's own runner writes.
  Nothing is inferred from model-authored text.
- SQLite busy and locked conditions are classified as `DatabaseBusy` on every path — including while
  acquiring a transaction and while rolling one back — and are never reported as a lost race. Generic
  faults claim no competing actor; only outcomes that establish a real competitor use race wording.
- No provider-capacity exhaustion is claimed. The shipped reader reports `Unknown` and says why.
- Viewing and refreshing the Demiplane mutates nothing. Every mutation is POST-only with antiforgery.

### Database reset

The development SQLite database was **intentionally deleted and rebuilt from migrations** on
2026-08-05. This environment is a development sandbox and its database held disposable data; a local
archive was taken outside the repository and neither it nor the database is committed.

Rebuilding from empty confirmed: all four migrations apply cleanly, `SessionHandoffs` and all 21
expected indexes exist (including `IX_AgentSessions_TaskId_Started` and
`IX_SessionHandoffs_TaskId_Pending`), no migrations remain pending, and no seed or startup process
creates historical state — every table starts empty.

### Operational baseline

Find Familiar now has an operational Demiplane baseline. The project **Find Familiar** was created on
the fresh database and its Demiplane verified end to end: Observatory, Waiting for you, Scrying pool
and Work map all render; "Waiting for you" is accurately empty; the Scrying pool reports Claude as
`Unknown` with its reason rather than inventing a number; five consecutive refreshes changed no row
and did not move `ContextRevision`. No live provider was invoked.

### Known follow-up (not a Sprint 10 blocker)

`StaleContext` may be returned to a losing concurrent approval when a winner commits between
preflight reads. `WorkApprovalService.ApproveAsync` reads the proposal and the project in two separate
statements; a contender that reads the proposal while it is still Pending and then reads the project
after the winner's commit has advanced `ContextRevision` is told its context went stale, when what
actually happened is that someone else approved.

Nothing is created either way and the loser carries no task or session links, so no guarantee is
broken — the message simply points at a refresh rather than at the approval that won. The fix belongs
to a later sprint: on a stale-context preflight, re-read the proposal and return `AlreadyApproved`
when it has become terminal, before reporting `StaleContext`. Recorded in ADR-0011.

---

## Sprint 11 — Conversational Familiar v0

**Accepted:** 2026-08-06 · **ADR:** [ADR-0012](decisions/ADR-0012-conversational-reasoning-and-human-confirmed-action.md)

### What this sprint established

A human can open `/Familiar/{projectId}`, read an accurate account of a project with no provider
configured at all, hold a conversation grounded in that project once one is, confirm a proposed task
or Planner session, and see that the resulting work is indistinguishable — to the queue, the runner
and the Demiplane — from work started by hand.

The sprint's central invariant holds structurally rather than by policy: **there is no code path by
which provider text changes persisted state without an explicit human confirmation and a
re-validation inside the confirming transaction.** `IWorkflowDispatchService` is referenced from
exactly one file under `Services/Familiar` — `FamiliarActionService`'s constructor.

### Accepted commit chain

| Commit | Slice |
|---|---|
| `a48e78d` | Read-only Familiar page |
| `44ed6a6` | Adapter fix — read-only sessions given read tools (see below) |
| `f4d35be` | Conversation flow and provider abstraction |
| `7ec4a57` | Human-confirmed actions |
| `b9db66f` | Portable reasoning provider |
| `41b35e1` | Read replies wrapped in a Markdown fence |
| `dcc1136` | systemd user services |

History is linear; no merge commits.

### Verification

`dotnet build` — 0 warnings, 0 errors. `dotnet test` — all pass, three consecutive full runs.
`dotnet ef migrations has-pending-model-changes` — clean. `git diff --check` — clean. No credential,
host, or machine-local path in the diff except the synthetic fixtures that exist to prove redaction.

No test in this repository opens a network connection. The reasoning providers are exercised entirely
through in-process stubs.

### Proven against a live model, not simulated

A real question about this project was answered by a real model through the shipped provider, and the
reply used all three registers the behaviour contract requires:

> **Recorded:** one task is blocked … its ReasonText says you marked it blocked but no reason is
> stored on the task itself. **Inferred:** nothing can progress until the condition is resolved and
> the flag cleared. **Unknown:** I cannot tell what that condition is or who set the block; the
> snapshot carries no history.

The task it cited is real. Round trip 5.7 seconds. Five distinct provider states appeared in one live
conversation — not configured, unreachable, response unusable, rate limited, and answered — each with
its exact authored wording, and the page stayed fully usable throughout.

Verified directly against the live database: `FamiliarMessages` has no column for a prompt, a
thinking block, a raw payload or a provider exception. The outbound request declared no tools.

### Two defects found by running it for real

**Read-only adapter sessions had no read tools.** `ClaudeArgumentBuilder` emitted `--tools ""` for
read-only mode, which removed `Read`, `Grep` and `Glob` along with the mutating tools. A Planner
session could not read the repository it was assigned, exited zero, and wrote a confident plan
describing a directory layout this repository has never had — fabricated content recorded as durable
canonical context. Fixed in `44ed6a6`; ADR-0007 amended. The write boundary was never the problem:
`Edit`, `Write` and `Bash` remain absent.

**Replies wrapped in a Markdown fence were discarded.** A model returned bare JSON for a short
snapshot and fenced JSON once the prompt reached full size. Fixed in `41b35e1`; the payload inside is
still parsed and validated strictly.

### Deliberate deviations from the plan

- **`FamiliarReasoningOutcome` carries `EvidenceIds`**, which `specification.md` §5's record listing
  omits. Required by the acceptance bar for dropping invented citations. Bare identifiers, so the
  application decides what each one is and what it is called.
- **A ninth failure code, `snapshot-unavailable`**, for a project that could not be read after the
  human message was already durable. The eight codes in `user-experience.md` §3 cover reasoning-provider
  failures; this is a busy database.
- **The native Anthropic provider was dropped before merge.** Written and building, but wiring it
  required either a shared-abstractions project or moving `Program.cs` into a new host project. An
  OpenAI-compatible proxy reaches Claude through the shipped provider, so a second implementation
  bought first-party prompt caching at the cost of a structural change. Recorded in ADR-0012.

### Known follow-ups (not blockers)

- `ProjectSnapshotOutcome.Unavailable` renders an honest page but has no test; hard to force a busy
  database through the page. The database-busy infrastructure now exists to cover it.
- The evidence **keep** path is proven by unit test and by an in-process stub; the live model chose
  not to populate the evidence array, so it has not been observed end to end against a real provider.
- `FamiliarMessageDelivery.Degraded` remains unused by design.
- **The Familiar can create work but cannot record a decision.** Neither action kind writes down what
  a conversation concluded — the gap most relevant to this project's stated purpose.

### Operational baseline

The server and runner worker run as systemd **user** services with lingering enabled, so they start
at boot and survive logout. Auto-restart verified by killing the server with `-9` and observing it
return on a new PID within twenty seconds.

`ASPNETCORE_URLS` binds loopback plus one Tailscale address rather than `0.0.0.0`. This application
has no authentication of its own, so reachability is deliberately limited to an already-authenticated
network; the LAN address was verified to refuse connections.

A local model was evaluated and rejected **for this host only**: prompt processing measured 3.6
tokens/sec on a 2010 Xeon with no AVX support, roughly half an hour per question. The requirement is
AVX2, and it is documented rather than discovered.

---

## Sprint 12 — The Familiar Speaks

**Accepted:** 2026-08-06 · **ADR:** [ADR-0013](decisions/ADR-0013-conversational-provider-split.md)

### What this sprint established

A human can open `/Familiar` on a phone across Tailscale, hold a streaming conversation about the
whole system rather than one project, close the tab mid-reply, reopen it on another device, and find
the reply either still arriving or already complete. The conversation is durable server-side state;
the browser is a view of it, never its owner.

The sprint's central separation is ADR-0013's: **the lane that talks and the lane that does work are
different seams with different providers.** `IFamiliarChatProvider` streams text and declares no
tools — the request type has no member for them — so there is no execution surface regardless of what
a reply says. It holds no `DbContext`, no `HttpContext`, and no reference to `IWorkflowDispatchService`.
The Runner and the Claude Code adapter were not touched by this sprint at all.

### Accepted commit chain

| Commit | Slice |
|---|---|
| `f6f3326` | Sprint 12 plan and ADR-0013 |
| `b3e85af` | Durable system-wide conversation |
| `e9b7cff` | Streaming conversational provider |
| `15aa72b` | Follow the model alias rather than a dated id |
| `d4c6856` | Standing brief, sensitivity boundary, usage panel |
| `f353e93` | Date the brief so it stops claiming the present |
| `0c20b82` | Rank open work above finished work |
| `a3b3125` | Capture the cached-input count |

History is linear; no merge commits.

### Verification

`dotnet test` — 907 passed, 0 failed, with the server and worker stopped, database backed up first.
`dotnet build` — 0 warnings, 0 errors. `dotnet ef migrations has-pending-model-changes` — clean.

No test in this repository opens a network connection. Cost isolation is **structural, not
conventional**, and was proven by exporting the real `XAI_API_KEY` into the test process and
observing that no test could reach a provider — `FamiliarChatCostIsolationTests`.

### Durability proven against real interruption, not simulated

Three invariants hold in the database rather than in code that means well:

- **One reply in flight per conversation**, held by a filtered unique index over
  `"State" IN ('Pending','Generating')`. A second send while one is arriving is `Attached` — the
  message stays in the composer, and nothing typed is lost.
- **A killed server does not strand a turn.** `RecoverAsync` sweeps at startup: `Generating` becomes
  `Failed` with `generation-interrupted`, `Pending` is re-enqueued. Verified by killing the server
  mid-reply with `-9`.
- **Resume is one cursor contract**, shared by the JSON read, the SSE stream, and the client, and it
  stops before a turn still arriving so a reload cannot skip a half-written reply.

Partial replies are kept. A stream that fails after emitting half an answer leaves the half that
arrived, with a note appended saying it stopped — the person is entitled to what was already said.

### Five provider states, each with its own wording

Not configured, unreachable, rate limited, malformed, unauthenticated — every one maps to exactly one
code and one authored sentence, and adding a status without wording fails a test. **No response body,
exception message, or header is ever read, logged, or persisted**; an error body routinely echoes the
request and can name a host, a path, or part of a key. The columns for prompts, raw payloads and
hidden reasoning do not exist.

### Two defects found by running it for real

**The Familiar described Sprint 11 as the present.** Asked for the state of the project, it gave a
fluent, confident, months-stale answer. Two separate causes, and only one was missing data:

- a genuine defect — the brief carried no time dimension at all, so a model shown state with no
  indication of its age described it in the present tense, correctly reasoning from what it was
  given. Fixed with `NewestRecordedActivityUtc`, an explicit limitation that records ending is
  evidence about recording and not about work, and a system-prompt rule against present-tense state
  claims;
- a data gap — Sprint 12 itself was unrecorded, since this project's work is mostly done in git and
  only sometimes tracked as a task. Backfilled as completed work.

**A burst of finished work hid everything outstanding.** Ordering fell through to recency, so a
sprint's worth of freshly-completed tasks pushed the one Ready and the one Blocked task out of the
capped list — "what is the state of things?" answered with four done things and nothing open. Recency
is a poor proxy for relevance exactly when a burst has just landed, which is exactly when someone
asks. Fixed in `0c20b82` by ranking open work above finished work.

**A third, found while closing the sprint:** the usage frame's cached-token field was parsed into a
record nothing read, so the dashboard said "not reported" against an endpoint that was sending the
number. It survived because no test fed the parser a byte of wire format. Fixed in `a3b3125`.

### The refusal, verified

Asked to mark a demonstrably-finished task complete, the Familiar declined and told the operator to
do it by hand. It wrote nothing: the project's context revision was unchanged at 46 afterwards. That
refusal is correct for Sprint 12 and is the specification for Sprint 13.

### Deliberate deviations from the plan

- **`FamiliarChat` / `FamiliarChatTurn`, not `FamiliarConversation` / `FamiliarTurn`.** Those names
  were already taken by Sprint 11's per-project Familiar. The two coexist rather than one being
  renamed into the other.
- **Token columns are `InputTokens` / `OutputTokens`**, not `Prompt*` / `Completion*`.
  `FamiliarConversationModelTests` rejects any column whose name contains "Prompt", to keep prompt
  text out of the database by construction. The guard was kept and the columns renamed, rather than
  the guard weakened.
- **Retrieval was not built.** `search_context` was sketched in the plan as a model-driven tool. It
  is deferred to Sprint 13 and reshaped as server-side deterministic retrieval, for reasons recorded
  in ADR-0014.

### Known follow-ups (not blockers)

- **The Familiar cannot read context entries or decisions.** The brief carries projects and tasks
  only, so results harvested since Sprint 9 — and the ADRs describing why this system is shaped the
  way it is — are invisible to it. The loop this project exists to close has been open the whole
  time. This is Sprint 13 slice 1 and the sharpest finding of this sprint.
- The cached-input percentage is honest but thin: only turns recorded after `a3b3125` carry the
  figure, so the dashboard's ratio understates caching until the older turns age out.
- `grok-4.1-fast` was retired mid-sprint. The configuration follows an alias now, and a retired model
  surfaces as a visible `chat-malformed` rather than a dead stream — xAI returns 400 for a bad
  credential, not 401, so that code names both causes and claims neither.

### Operational baseline

`XAI_API_KEY` lives in `/srv/familiar/secrets/familiar.secrets.env`, mode 0600, single underscores.
The name matters: a double underscore is ASP.NET's section separator and would bind the secret into
`IConfiguration`, where a configuration dump would print it. Configuration names the variable; the
value is read from the environment and never through configuration.

Zero Data Retention is enabled on the account. It costs this architecture nothing, because the server
owns all conversation state and no stateful provider feature is used — no Responses API, no Files, no
Collections, no Batch.
