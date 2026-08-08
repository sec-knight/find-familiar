# The Summoning Gate: connecting an external AI client

How to let a frontier client — ChatGPT first — read this Familiar's durable memory.

The architecture and the reasoning behind it are in
[ADR-0016](decisions/ADR-0016-the-summoning-gate-and-interchangeable-bodies.md). This document is
product-specific setup and deliberately kept apart from it: the gateway is provider-neutral, and only
the last section is about ChatGPT.

**Sprint 14 is read-only.** Nothing described here can create a task, start a session, approve a
plan, or write a memory.

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
| `familiar_manifest` | `GET /api/gateway/manifest` | Name, description, capabilities, empty write list |
| `search_familiar_context` | `POST /api/gateway/context/search` | Up to 6 recorded items with ids, excerpts, dates, and a disclosure sentence |
| `list_familiar_projects` | `GET /api/gateway/projects` | Readable projects and a count of withheld ones |
| `get_project_context` | `GET /api/gateway/projects/{id}` | One project's shape, tasks needing attention, newest record date |

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

**As of Slice 1 no operation accepts `familiar.decide`.** The boundary exists so it can be reviewed
before anything consequential stands behind it; the decision tools are a later slice. A client may
request and be granted the scope today, and will find nothing that consumes it.

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
6. Confirm the connector lists four tools.
7. In a new chat, enable the connector for that conversation.

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

- **Read and fetch only.** No write-back memory, no action execution, no task creation, no session
  start. ChatGPT Pro does not currently support MCP writes, and this server would refuse them if it
  did.
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
- **`familiar.decide` grants no operation yet.** It can be requested and granted today and nothing
  consumes it. The decision tools are a later slice.
- **A restart forgets spent refresh tokens.** Replay protection is in memory, so a refresh token
  captured before a restart could be redeemed once after it. ADR-0017 §4 records why that trade is
  taken and what would reverse it.
