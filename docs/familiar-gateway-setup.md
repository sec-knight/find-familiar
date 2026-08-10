# The Summoning Gate: connecting an external AI client

How to let a frontier client — ChatGPT first — read this Familiar's durable memory.

The architecture and the reasoning behind it are in
[ADR-0016](decisions/ADR-0016-the-summoning-gate-and-interchangeable-bodies.md). This document is
product-specific setup and deliberately kept apart from it: the gateway is provider-neutral, and only
the last section is about ChatGPT.

**Almost everything here is read-only.** One operation is not: `submit_familiar_decision` relays a
decision the human has explicitly made, to a workflow gate Find Familiar itself raised. It requires
its own OAuth scope, it accepts no free text, and Find Familiar re-decides legality independently.
Nothing here can create a task, start arbitrary work, edit a record, or write a memory.

---

## 1. Choose the Familiar's identity

Three settings, under `Familiar:Identity`. Only the name is usually worth setting.

| Setting | Meaning | Default |
|---|---|---|
| `Familiar__Identity__Name` | What the Familiar is called. | `Familiar` |
| `Familiar__Identity__Description` | One sentence on what it is. | A sensible generic description |
| `Familiar__Identity__Guidance` | Optional note on register, max 500 characters. | none |

In `/srv/familiar/config/familiar-server.env`:

```ini
Familiar__Identity__Name=Sakura
Familiar__Identity__Guidance=Speak plainly and briefly. Say what the records show and when they end.
```

`Guidance` is a note on register, not a system prompt — see ADR-0016 §5 for why it is capped.

## 2. Create the gateway secret

The gateway uses **its own** bearer token, separate from `FAMILIAR_RUNNER_TOKEN`. That separation is
deliberate: the runner token stays on this machine and the tailnet, while this one is held by a
frontier vendor and crosses the public internet on every call.

Generate one:

```bash
openssl rand -base64 48 | tr -d '\n=' | tr '+/' '-_'
```

Put it in the secrets file, never in `appsettings.json` and never in git:

```bash
# /srv/familiar/secrets/familiar.secrets.env   (chmod 600)
FamiliarGateway__Enabled=true
FamiliarGateway__Token=<the generated value>
```

Restart:

```bash
systemctl --user restart familiar-server.service
```

Requirements the server enforces:

- Minimum 32 characters. A shorter token is refused at startup of every request, because a token
  short enough to guess is worse than none — it looks like security.
- With `Enabled=false`, **no gateway route is mapped at all**. Probes get 404, not 401.
- With `Enabled=true` and no usable token, every call is refused. It never fails open.

### Rotation

1. Generate a new value and replace `FamiliarGateway__Token` in the secrets file.
2. `systemctl --user restart familiar-server.service`.
3. Reconnect the connector in ChatGPT (§7). Rotation also invalidates every OAuth token this server
   has issued, because their signing key is derived from this secret — so an OAuth connector must be
   re-approved, not merely edited.

There is no grace period and no second accepted token: rotation is immediate, and any connected
client stops working until it is updated. That is intentional for a one-user deployment — a
revocation that takes effect later is not a revocation.

To revoke entirely, set `FamiliarGateway__Enabled=false` and restart. The routes disappear.

## 3. Verify locally before exposing anything

Everything below runs against `127.0.0.1` and needs no network change.

```bash
TOKEN='<your gateway token>'
BASE='http://127.0.0.1:5199'

# Must be 401 — the gate refuses before it reads anything
curl -s -o /dev/null -w '%{http_code}\n' "$BASE/api/gateway/manifest"

# Identity
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/gateway/manifest"

# Projects
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/gateway/projects"

# The acceptance question
curl -s -X POST "$BASE/api/gateway/context/search" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"query":"where did we leave off on Find Familiar"}'
```

And the MCP transport itself:

```bash
curl -s -X POST "$BASE/mcp" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

Expect four tools, each with `readOnlyHint: true`.

## 4. What the gateway exposes

| MCP tool | REST | Returns |
|---|---|---|
| `familiar_manifest` | `GET /api/gateway/manifest` | Name, description, and the read and write capabilities this gateway declares |
| `search_familiar_context` | `POST /api/gateway/context/search` | Up to 6 recorded items with ids, excerpts, dates, and a disclosure sentence |
| `list_familiar_projects` | `GET /api/gateway/projects` | Readable projects and a count of withheld ones |
| `get_project_context` | `GET /api/gateway/projects/{id}` | One project's shape, tasks needing attention, newest record date, and the project's own recorded context enumerated |
| `open_decisions` | `GET /api/gateway/decisions` | What is waiting on the human — session handoffs and plan proposals alike: reason, evidence, what a plan would create, legal choices, and the identifiers a decision needs |
| `get_task_detail` | `GET /api/gateway/tasks/{id}` | One task in full: state and reason, the sessions that ran, the records they produced, and any decision it awaits |
| `inspect_familiar_runtime` | `GET /api/gateway/runtime` | Workers, per-role readiness and provider capacity — why a role work is waiting on can or cannot start |
| `create_familiar_project` | `POST /api/gateway/lifecycle/projects` | Creates a project. **Requires `familiar.project.write`.** |
| `create_familiar_task` | `POST /api/gateway/lifecycle/tasks` | Creates a Ready task. Starts nothing. **Requires `familiar.project.write`.** |
| `set_familiar_task_status` | `POST /api/gateway/lifecycle/tasks/{id}/status` | Blocks, unblocks or completes a task. **Requires `familiar.project.write`.** |
| `record_familiar_context` | `POST /api/gateway/lifecycle/context` | Records a note against a project or task. **Requires `familiar.project.write`.** |
| `start_familiar_session` | `POST /api/gateway/workflow/sessions` | Starts a Planner, Implementer or Reviewer session. **Requires `familiar.workflow.start`.** |
| `cancel_familiar_session` | `POST /api/gateway/workflow/control/cancel` | Cancels a running session, with the human's reason. **Requires `familiar.workflow.control`.** |
| `submit_familiar_decision` | `POST /api/gateway/decisions/submit` | Relays a decision the human explicitly made, for either decision kind. A plan is approved exactly as drafted — no item can be added, removed or reworded. **Requires `familiar.decide`.** |

Every response is bounded, carries stable ids, and states what it could not show. A search that finds
nothing says so explicitly, and says whether near-misses existed — a client that is handed an empty
list with no explanation will fill the silence from general knowledge.

## 5. Network exposure — read before changing anything

The server listens on `127.0.0.1:5199` and the tailnet address only. ChatGPT's servers call from the
public internet, so reaching them means publishing something — and publishing the Kestrel application
would publish `/Familiar`, `/Demiplane` and `/api/runner` along with it.

**The exposure is path-scoped, not host-scoped.** Tailscale Funnel's `--set-path` mounts individual
paths; anything not mounted is answered by tailscaled with a 404 and never reaches Kestrel. Each of
the following is its own mount, and each one is required:

```bash
# Requires root. The MCP endpoint itself:
sudo tailscale funnel --bg --yes --set-path=/mcp http://127.0.0.1:5199/mcp

# OAuth discovery. Prefix mounts, so each also covers its /mcp path-inserted form:
sudo tailscale funnel --bg --yes \
  --set-path=/.well-known/oauth-protected-resource \
  http://127.0.0.1:5199/.well-known/oauth-protected-resource
sudo tailscale funnel --bg --yes \
  --set-path=/.well-known/oauth-authorization-server \
  http://127.0.0.1:5199/.well-known/oauth-authorization-server

# OAuth registration, consent and tokens:
sudo tailscale funnel --bg --yes --set-path=/oauth/register  http://127.0.0.1:5199/oauth/register
sudo tailscale funnel --bg --yes --set-path=/oauth/authorize http://127.0.0.1:5199/oauth/authorize
sudo tailscale funnel --bg --yes --set-path=/oauth/token     http://127.0.0.1:5199/oauth/token
```

Check what is actually published with `tailscale funnel status`, and confirm the boundary holds:

```bash
# Each must be 404 from tailscaled, with and without a token
for p in / /health /Familiar /api/runner /api/gateway/manifest /Demiplane; do
  printf '%-26s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' https://your-host.ts.net$p)"
done
```

**Never use bare `tailscale funnel 5199`.** It publishes the entire application.

The REST adapter (`/api/gateway/*`) is deliberately not published. It exists to make the gateway
verifiable with `curl` from the tailnet and has no reason to be reachable from outside.

To close everything: `sudo tailscale funnel reset`.

## 6. OAuth (Sprint 14.1)

The static bearer token still works and is still how you verify locally. What OAuth adds is a way for
a client that speaks the MCP authorization specification — ChatGPT's connector flow among them — to
obtain a token for itself, so the gateway secret never has to be typed into a vendor's form.

The reasoning is in [ADR-0017](decisions/ADR-0017-oauth-for-the-summoning-gate.md). One setting turns
it on:

```ini
# /srv/familiar/config/familiar-server.env
# Scheme and host only. No path, no trailing slash. Never inferred from the request.
FamiliarGateway__PublicBaseUrl=https://familiar.example.ts.net
```

Unset means no OAuth routes are mapped at all. Restart the server after setting it.

Optional, with sensible defaults:

| Setting | Meaning | Default |
|---|---|---|
| `FamiliarGateway__AccessTokenLifetimeSeconds` | How long an access token lives | `3600` |
| `FamiliarGateway__RefreshTokenLifetimeDays` | How long a refresh token lives | `30` |
| `FamiliarGateway__AllowedRedirectHosts` | Hosts a client may register a callback on | `chatgpt.com,chat.openai.com,openai.com` |

`AllowedRedirectHosts` is the open-redirection control. Dynamic client registration lets an
unauthenticated caller nominate where an authorization code is delivered; this is what stops that
being anywhere. Widen it only for a host you intend to hand your context to.

### Scopes

Two, and they are deliberately not one grant.

| Scope | What it permits |
|---|---|
| `familiar.read` | Read projects, their state, and recorded context. Nothing else. |
| `familiar.decide` | Relay a decision **you** have explicitly made to a workflow gate that already exists. |
| `familiar.project.write` | Create a project or task, change a task's status, record a note. Starts nothing. |
| `familiar.workflow.start` | Start a Planner, Implementer or Reviewer session on a task you already have. |
| `familiar.workflow.control` | Cancel a session that is running. |

**Why separate.** A conversational client reads constantly and decides rarely, and those deserve
different answers from the person granting them. Folded into one grant, every read connection would
silently carry the ability to act, and the consent screen could no longer honestly say "read-only" to
anyone.

**What `familiar.decide` is not.** It is not write access, and it does not make the client or the
model an authority. It is permission to *ask* — to carry your stated choice, against one identified
object at one observed revision, to the same service the Demiplane posts to. Find Familiar
re-validates legality inside the transaction regardless, so a client holding this scope is a courier,
never a decision-maker. There is no `familiar.write`, and no scope grants arbitrary task mutation,
workflow dispatch, or filesystem access.

**The static gateway token is read-only, permanently.** It does not expire, it is bound to no browser
flow, and nobody approved a consent screen to obtain it — so it cannot be evidence that a human
decided anything. It satisfies `familiar.read` and can never satisfy `familiar.decide`.

**A grant cannot widen itself.** A refresh may narrow the scopes it holds and may never add one; a
request naming a scope this server does not issue is refused outright rather than quietly reduced.
Raising a grant requires going through consent again.

**Each write scope is independent.** Holding one grants nothing about the others: a project-write
token cannot start work or answer a decision, a start token cannot create a task or cancel one, and a
control token cannot start anything. The consent screen shows one block per requested capability,
each stating what it cannot do.

**Deliberately not exposed at any scope:** enabling or disabling a worker, and capturing a session's
result on a worker's behalf. The first is operator administration rather than project work; the second
is the worker's own report, and a client writing it would be fabricating evidence the reviewer then
reads.

**Exactly one operation accepts `familiar.decide`:** `submit_familiar_decision`. It takes a decision
id, the concurrency token from `open_decisions`, and a choice of `approve` or `decline` — no free
text, no note, nothing a model could fill with words the human never said. It delegates to the same
approval transaction the Demiplane's own button posts to, which re-decides legality independently and
may refuse. A token from a view that has since moved is rejected rather than applied.

### What the flow looks like

1. ChatGPT fetches `/.well-known/oauth-protected-resource/mcp` (or is pointed at it by the
   `WWW-Authenticate` header on a 401 from `/mcp`).
2. It fetches `/.well-known/oauth-authorization-server` and registers itself at `/oauth/register`.
3. It opens `/oauth/authorize` in your browser. **You paste the gateway token into the consent
   screen** — that is the one moment the secret is used, and it goes to this server, not to ChatGPT.
   The screen states exactly which scopes are being requested; if `familiar.decide` is among them it
   says so separately and explains that it lets the client relay your decisions, not make them.
4. It exchanges the code at `/oauth/token` with PKCE, and gets an access token bound to this server
   as its audience.

Rotating `FamiliarGateway__Token` invalidates every issued token immediately, because the key that
signs them is derived from it. There is no separate revocation step.

## 7. Connect it in ChatGPT

Requires ChatGPT Pro, Team, Enterprise or Edu, and Developer Mode.

1. **Settings → Connectors → Advanced → Developer mode**, and turn it on.
2. **Settings → Connectors → Create** (or **Plugins → Add plugin → Custom MCP Server**).
3. Name it (for example `Sakura`), and set the MCP server URL to your public HTTPS endpoint ending in
   `/mcp` — e.g. `https://familiar.example.ts.net/mcp`.
4. Choose **OAuth** as the authentication type. Leave client ID and secret blank: this server
   supports dynamic client registration, so ChatGPT registers itself. If the form insists on a bearer
   token instead, the static gateway token still works.
5. ChatGPT opens the consent screen. Paste the gateway token there and approve.
6. Confirm the connector lists the tools this server currently exposes. Check the count against the
   server rather than against a number written here — this document will go stale and the server will
   not:

   ```bash
   curl -s -X POST https://familiar.example.ts.net/mcp \
     -H "Authorization: Bearer $FAMILIAR_GATEWAY_TOKEN" \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
     | sed 's/^data: //' | python3 -c 'import json,sys; t=json.load(sys.stdin)["result"]["tools"]; print(len(t)); [print(x["name"]) for x in sorted(t, key=lambda i: i["name"])]'
   ```

   If ChatGPT shows fewer tools than that command prints, the connector is serving a cached tool list.
   See §7.1.
7. In a new chat, enable the connector for that conversation.

## 7.1 When ChatGPT does not see a newly added tool

**ChatGPT caches the tool list at registration time.** Adding a tool to this server does not reach an
existing connector, and neither does an in-place "refresh" or an MCP reset — both have been observed
to keep serving a tool list captured when the connector was first created, including tool
*descriptions* from that moment. The symptom is a connector whose tool set matches an older commit of
`FamiliarMcpTools.cs`: a missing tool, and descriptions that contradict tools that are present.

Confirm it is a cache rather than a server problem first — if the `curl` above lists the tool, the
server is serving it to anyone who asks, including ChatGPT:

```bash
# Same command against localhost; the two responses should be identical.
curl -s -X POST http://127.0.0.1:5199/mcp ... | wc -c
```

**The fix is a full re-registration, not a refresh:**

1. **Settings → Connectors**, open the Find Familiar connector, and **delete** it.
2. Recreate it from step 2 above, with the same URL.
3. Paste the gateway token at the consent screen again.
4. Re-enable it in each conversation that used it.

Nothing needs to change on this server, and no token needs rotating: OAuth artifacts here are
stateless and signed, so there is no server-side registration record to clear. Deleting the connector
discards ChatGPT's cached copy, which is the only stale thing in the system.

### The acceptance phrase

> Sakura, where did we leave off on Find Familiar?

ChatGPT should call `search_familiar_context` and answer from what comes back.

If it answers without calling the tool, say so explicitly — "check my Familiar" — and confirm the
connector is enabled for that conversation. Tool descriptions encourage selective use; they cannot
compel it.

### Disabling

- **Per conversation:** turn the connector off in the composer.
- **Per account:** delete the connector in Settings → Connectors.
- **At the server, authoritatively:** set `FamiliarGateway__Enabled=false` and restart. This is the
  only one that does not depend on the vendor honouring anything.

## 8. Limitations

- **One narrow write, and no others.** `submit_familiar_decision` relays a decision the human has
  already made to a gate Find Familiar itself raised. Everything else is read-only: no write-back
  memory, no task creation, no session start, no arbitrary action execution, and no way for the model
  to decide anything on its own — it requires the separate `familiar.decide` scope and accepts no
  free text.
- **ChatGPT decides when to retrieve.** The tool descriptions say when to call and when not to, and
  the model may still call too often or not at all.
- **Behaviour varies by body.** The same Familiar and the same context produce different answers
  through ChatGPT, Claude, or the native page. Continuity is in the context, not in the phrasing.
- **Sensitivity is absolute and one-directional.** Sensitive projects and entries are never returned,
  only counted. A count discloses that something exists and nothing about what.
- **Records are not reality.** Every project snapshot carries the date of its newest record. Work done
  without being recorded is invisible, and an external model should be told to say so.
- **Repository awareness is a listing, not the code.** The snapshot carries branch, head, recent
  commit subjects and tracked paths — never file contents or diffs.
- **Reading is still all-or-nothing.** `familiar.read` grants everything non-sensitive; there is no
  per-project or per-category read grant. What Slice 1 split off is the ability to act, not the ability
  to read.
- **`familiar.decide` grants exactly one operation**, and only for decisions Find Familiar is already
  waiting on a human for. It cannot create a task, start arbitrary work, edit a record, or take any
  decision the workflow has not itself raised.
- **A restart forgets spent refresh tokens.** Replay protection is in memory, so a refresh token
  captured before a restart could be redeemed once after it. ADR-0017 §4 records why that trade is
  taken and what would reverse it.
