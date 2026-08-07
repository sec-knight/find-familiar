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
3. Update the connector in ChatGPT (§6) with the new value.

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

**The server currently listens on `127.0.0.1:5199` and the tailnet address only.** ChatGPT's servers
call your MCP endpoint from the public internet, so they cannot reach it as it stands.

**No networking change has been made, and none should be made without deciding this deliberately.**
See the decision package in the Sprint 14 report: Tailscale Funnel, a Cloudflare Tunnel, and a
reverse proxy have materially different exposure profiles. Whichever is chosen:

- Terminate TLS. ChatGPT requires HTTPS; the bearer token is in a header and must not cross the
  internet in clear text.
- Expose **only** `/mcp`. Nothing else on this server — not `/Demiplane`, not `/Familiar`, not
  `/api/runner` — should become publicly reachable.
- Keep the existing Tailscale posture for everything else.

## 6. Connect it in ChatGPT

Requires ChatGPT Pro, Team, Enterprise or Edu, and Developer Mode.

1. **Settings → Connectors → Advanced → Developer mode**, and turn it on.
2. **Settings → Connectors → Create**.
3. Name it (for example `Sakura`), and set the MCP server URL to your public HTTPS endpoint ending in
   `/mcp` — e.g. `https://familiar.example.ts.net/mcp`.
4. Choose authentication and supply the gateway token as a bearer token.
5. Save, then confirm the connector lists four tools.
6. In a new chat, enable the connector for that conversation.

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

## 7. Limitations

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
- **One token, all-or-nothing.** Anyone holding the gateway token can read everything non-sensitive.
  There is no partial grant; that needs OAuth, and ADR-0016 records when it becomes worth it.
