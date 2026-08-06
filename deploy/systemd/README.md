# Running Find Familiar as a service

Two **systemd user services** — the server and the runner worker. User services rather than system
ones because nothing here needs root, and a home lab should not have to hand a test application
privileges it never uses.

With lingering enabled the services start at boot and survive logout, so the application stays
reachable without anybody being signed in.

---

## Why the files are split three ways

| File | Contains | Mode | Committed? |
|---|---|---|---|
| `*.service` | How to run it | 0644 | ✅ yes |
| `*.env` | Addresses, model, provider choice | 0644 | ✅ yes (as `.example`) |
| `secrets.env` | Tokens and API keys | **0600** | ❌ **never** |

The split is the point. Configuration is something you want to read, diff and version; a credential
is not. `systemd` loads both, so nothing is lost by keeping them apart — and the application only
ever learns the *name* of the variable holding a key, never a key from a config file.

---

## Install

```bash
# 1. Copy the units
mkdir -p ~/.config/systemd/user
cp deploy/systemd/familiar-*.service ~/.config/systemd/user/

# 2. Copy and edit the non-secret configuration
sudo mkdir -p /srv/familiar/config /srv/familiar/logs /srv/familiar/secrets
cp deploy/systemd/familiar-server.env.example /srv/familiar/config/familiar-server.env
cp deploy/systemd/familiar-worker.env.example /srv/familiar/config/familiar-worker.env
$EDITOR /srv/familiar/config/familiar-server.env    # set ASPNETCORE_URLS, model, provider

# 3. Write the secrets, readable only by you
umask 077
cat > /srv/familiar/secrets/familiar.secrets.env <<'EOF'
RunnerBridge__Token=...
FAMILIAR_RUNNER_TOKEN=...
OPENROUTER_API_KEY=...
EOF
chmod 600 /srv/familiar/secrets/familiar.secrets.env

# 4. Survive logout and reboot
loginctl enable-linger "$USER"

# 5. Start
systemctl --user daemon-reload
systemctl --user enable --now familiar-server.service
systemctl --user enable --now familiar-worker.service
```

`loginctl enable-linger` normally needs no root for your own user. Without it the services stop the
moment you log out, which defeats the purpose.

The units point at `bin/Debug/…` because that is what a development checkout builds. For anything
long-lived, `dotnet publish -c Release` and update `ExecStart`.

---

## Everyday commands

```bash
systemctl --user status familiar-server.service
systemctl --user restart familiar-server.service      # after editing an env file
journalctl --user -u familiar-server.service -f       # or tail the log below
tail -f /srv/familiar/logs/familiar-server.log
```

Editing an env file does **not** take effect until the service restarts — `daemon-reload` reloads
unit files, not environment files.

---

## Two settings worth understanding

**`ASPNETCORE_URLS` is your only access control.** This application has no authentication of its own.
The shipped example binds loopback plus one Tailscale address:

```
ASPNETCORE_URLS=http://127.0.0.1:5199;http://YOUR-TAILSCALE-IP:5199
```

That reaches a tablet on an already-authenticated network while leaving the LAN unable to see it at
all. `0.0.0.0` would expose task creation, session starts and proposal confirmation to every device
on your network — choose it deliberately if you choose it.

**`WorkingDirectory` is load-bearing.** `App_Data` — the SQLite database and the data-protection keys
— is resolved relative to it. A unit that starts from the wrong directory silently creates a second,
empty database rather than failing.

---

## Restart behaviour

| Service | Policy | Why |
|---|---|---|
| Server | `on-failure`, 5s | A clean stop is intentional; a crash is not. |
| Worker | `always`, 15s | It polls and retries by design, so a server restart is not a reason to stay down. The longer delay keeps it from spinning while the server comes back. |

Verify it actually works rather than trusting the file — kill the process and watch it return:

```bash
kill -9 "$(systemctl --user show -p MainPID --value familiar-server.service)"
sleep 20 && systemctl --user show -p NRestarts --value familiar-server.service
```

---

## The worker's easily-missed variable

`FAMILIAR_CLAUDE_RUNTIME_PATH` must be an absolute path to the native `claude` executable. The Runner
supplies the adapter's worktree, allowed root and mode per invocation, but **not** this one — it is
inherited from the worker's own environment.

A worker started without it looks healthy: it registers, heartbeats, and claims sessions normally.
Every session then fails at adapter configuration. If sessions are being claimed and dying, check
this first.
