# Worker runtime guide

How to run a Familiar worker that discovers, claims and executes eligible sessions automatically.
See ADR-0008 for why each boundary exists, and the Claude worker operator guide for the adapter's
own configuration and troubleshooting.

Every value below is a placeholder. Never commit real tokens, credentials, machine paths, or
tailnet identifiers.

## What automatic pickup changes

Before, each session needed a manual relay: copy the task ID, copy the session ID, run the runner.
Now a registered worker polls Familiar, is granted one session at a time, and executes it through
the same runner and adapter as before.

The explicit invocation still works and is unchanged — automatic pickup is an additional mode, not
a replacement.

What the worker still does **not** do: it never starts a session, never chooses a role, never
chains Planner to Implementer, and never completes a task. Those remain your decisions in the
`/Work` queue.

## Prerequisites

- Everything in the Claude worker operator guide (authenticated Claude Code, the native executable
  path, a built checkout of this repository).
- The Familiar server reachable from this machine over your private tailnet only.
- At least one Familiar project whose repository exists on this machine.

## Configuration

Two things, deliberately separated:

```text
FAMILIAR_RUNNER_TOKEN    the Familiar runner bridge credential   (environment variable only)
FAMILIAR_WORKER_CONFIG   absolute path to worker.json            (machine-local file)
```

The credential is **never** a field in `worker.json`. Keep it in the process environment or an OS
secret store, so the configuration file never holds a secret.

`worker.json` is machine-local and must never be committed — it contains repository paths. The
repository ignores `worker.json` and `worker.*.json` anywhere in the tree. Copy
`docs/worker.example.json` and edit it:

```jsonc
{
  "baseUrl": "<tailnet-https-url>",
  "workerKey": "<stable-worker-identifier>",
  "displayName": "<human-readable-name>",
  "capabilities": ["Planner"],
  "adapterPath": "<absolute-path-to-FindFamiliar.Adapter.Claude.exe>",
  "adapterTimeoutSeconds": 600,
  "pollSeconds": 15,
  "maxPollSeconds": 120,
  "heartbeatSeconds": 60,
  "projects": [
    {
      "projectId": "<familiar-project-guid>",
      "worktree": "<absolute-path-to-repository>",
      "allowedRoot": "<absolute-path-to-parent-of-repository>",
      "mode": "read-only"
    }
  ]
}
```

Notes:

- `workerKey` is your durable identity. Keep it stable across restarts — changing it registers a
  second worker.
- `capabilities` lists the roles this worker will accept. Start with `["Planner"]`.
- `projects` is the repository mapping. **Only** projects listed here are ever offered to this
  worker, and the paths never leave this machine — the worker sends project GUIDs to the server,
  never paths.
- `mode` must be `read-only`. Automatic pickup refuses to start with `edit-worktree`; run those
  explicitly, by hand, so a claimed session can never write to a repository unattended.
- The adapter still enforces its own containment (`allowedRoot`, worktree checks). The worker
  chooses *which* repository; the adapter still decides whether that repository is allowed.

Adapter variables (`FAMILIAR_CLAUDE_RUNTIME_PATH`, `FAMILIAR_CLAUDE_ENTRYPOINT`,
`FAMILIAR_CLAUDE_TIMEOUT_SECONDS`, `FAMILIAR_CLAUDE_EXTRA_ARGS`) are still set in the worker's own
environment. The worktree, allowed root and mode are supplied per session by the worker from the
mapping above, so you no longer export one fixed repository globally.

## Starting the worker

Foreground, for diagnostics:

```text
dotnet FindFamiliar.Runner.dll worker
```

The worker prints its identity, capability list, project count and poll interval on startup, then
one line per claim and per completed execution. Diagnostics are fixed non-secret strings — never
prompts, model output, credentials, or repository paths.

Heartbeats continue during idle polling backoff and while the adapter is running. During execution
the worker also renews its claim before expiry. A failed heartbeat prevents a claim request; a
failed renewal stops the adapter so stale work is not submitted.

## Stopping the worker

Press **Ctrl+C**, or stop the service. Shutdown is cooperative: the current adapter process tree is
terminated and awaited through the same cancellation token the engine honors, and only then does
the loop exit. The worker logs `worker: stopped cleanly.` and exits `0`.

If a worker is killed hard mid-execution, its claim is recovered automatically once the lease
expires — no operator action is needed.

## Checking status

In Familiar, open **Workers**. For each registered worker you see:

- availability — `Online` (heartbeat within 90s), `Stale` (within 10 minutes), `Offline` beyond;
- enabled state;
- capabilities and last heartbeat;
- the active claim, linked to its task;
- lease timing, flagged when expired.

Availability is derived from the heartbeat and is diagnostic only — it never decides whether a
session may run.

To park a worker without deleting it, set `Enabled` to false on its row. It keeps registering and
heartbeating but is refused every claim; a heartbeat never re-enables it.

## Persistent hosting

A worker is an ordinary long-running console process. Both options below are optional — running it
in a terminal is entirely valid.

### Ubuntu (systemd)

`/etc/systemd/system/familiar-worker.service`, placeholders only:

```ini
[Unit]
Description=Find Familiar worker
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=<service-account>
WorkingDirectory=<absolute-path-to-runner-output-directory>
Environment=FAMILIAR_WORKER_CONFIG=<absolute-path-to-worker.json>
Environment=FAMILIAR_CLAUDE_RUNTIME_PATH=<absolute-path-to-claude-executable>
EnvironmentFile=<absolute-path-to-token-env-file>
ExecStart=/usr/bin/dotnet <absolute-path-to>/FindFamiliar.Runner.dll worker
Restart=on-failure
RestartSec=30

[Install]
WantedBy=multi-user.target
```

Put `FAMILIAR_RUNNER_TOKEN=<credential>` in the `EnvironmentFile`, owned by the service account and
mode `0600`, so the credential is not visible in the unit file or in `systemctl show`.

```text
sudo systemctl daemon-reload
sudo systemctl enable --now familiar-worker
systemctl status familiar-worker
journalctl -u familiar-worker -f
sudo systemctl stop familiar-worker
```

### Windows (Scheduled Task)

A Scheduled Task is sufficient; a Windows Service is not required and is not worth the extra
complexity for one worker.

- Trigger: **At log on** of the account that owns the authenticated Claude Code installation.
- Action: `dotnet.exe` with arguments `<absolute-path-to>\FindFamiliar.Runner.dll worker`, started
  in the runner's output directory.
- Run only when the user is logged on. Claude Code is authenticated per user, so a task running as
  `SYSTEM` or with stored credentials will not have that authentication.
- Settings: restart on failure, do not stop the task if it runs longer than expected.

Set `FAMILIAR_WORKER_CONFIG`, `FAMILIAR_RUNNER_TOKEN` and the adapter variables as **user**
environment variables for that account. Verify with `Get-ScheduledTask -TaskName <name>` and check
the worker appears as `Online` on the Workers page.

## Troubleshooting

- **Worker never appears on the Workers page** — the heartbeat is failing. Check `baseUrl`,
  tailnet reachability, and `FAMILIAR_RUNNER_TOKEN`. A `401` means the credential is wrong; a `503`
  means the server has no `RunnerBridge__Token` configured.
- **Worker is Online but never claims anything** — nothing is eligible. Confirm a session is
  `Started`, that its role is in `capabilities`, and that its project GUID is in `projects`.
- **`claim refused ... unknown worker, disabled worker, or bad credential`** — the worker was
  disabled by an administrator, or the registration was removed. Check the Workers page.
- **`claimed work has no local repository mapping`** — `worker.json` changed while a claim was in
  flight. The lease expires and the session returns to the queue; fix the mapping and restart.
- **A session shows an expired lease and is not progressing** — the worker died mid-run. It will be
  re-claimed automatically once the lease expires. Nothing was captured, so nothing is lost.
- **`claim renewal failed`** — the server could no longer confirm this exact claim generation (for
  example, the lease expired, the worker was disabled, or connectivity was lost). The worker stops
  the adapter and leaves the `Started` session for lease recovery.
- **Work executes but the session is cancelled with a Handoff entry** — the adapter failed. The
  category is in the entry and in the worker's stderr; see the Claude worker operator guide's exit
  codes.

## Safety notes

- Repository paths, adapter paths and credentials never reach the server. The database stores only
  a worker identity, capabilities and timestamps.
- The Familiar token is removed from the adapter's child environment, so it never reaches Claude.
- Automatic pickup is read-only. Editing, committing and pushing remain manual and reviewed.
- Every result and cancellation from the worker carries the claim's fencing token. An expired or
  replaced claim cannot change the session even if a stale process later regains connectivity.
- Familiar and the worker stay on the private tailnet. Do not expose either publicly.
