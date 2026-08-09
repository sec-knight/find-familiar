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
chains Planner to Implementer, and never completes a task. Those remain your decisions.

Since Sprint 09 the chaining decision is one click instead of four. When a session finishes,
Familiar records the step it proposes next and shows it on the task page; nothing runs until you
approve it. The worker is not involved in that decision and cannot make it — a worker holding
`Implementer` capability will sit idle next to an unapproved Implementer step. See the
[Talk workflow guide](talk-workflow-guide.md) for the workflow and
[ADR-0010](decisions/ADR-0010-human-gated-role-handoff.md) for why the gate is where it is.

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
- `capabilities` lists the roles this worker will accept. List every role you intend to approve
  handoffs for — usually all three. **An approved step whose role no worker declares stays `Started`
  and blocks its task**, because a task may hold only one Started session; you then have to notice it
  in `/Work` and capture or cancel it by hand.
- `projects` is the repository mapping. **Only** projects listed here are ever offered to this
  worker, and the paths never leave this machine — the worker sends project GUIDs to the server,
  never paths.
- `mode` is `read-only` or `edit-worktree`, and defaults to `read-only`.

  `edit-worktree` lets an **approved** Implementer session change files in that worktree. Opting a
  project in does not make every session a writing one: Planner and Reviewer stay read-only even
  here, because neither of their jobs is to change files.

  Edit mode requires a **clean linked git worktree** and refuses to start otherwise, so a run never
  mixes its changes with yours. It cannot commit or push: the adapter's tool list excludes `Bash`, so
  there is no path to `git` from inside the model's turn. You review the working tree afterwards and
  decide what to keep.

  A session only ever writes if a human approved that specific step. If you would rather review a
  plan before any file changes, leave the mapping `read-only` and run Implementer work explicitly.
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

## Session workspace lifecycle

For a mapping with `projectPath`, the worker creates a new detached Git worktree for every claimed
session under `allowedRoot`. The source checkout is checked for a clean status first, and the new lease
starts from its current `HEAD`; the old `worktree` path is not reused as a mutable session workspace.
Planner and Reviewer leases use read-only mode, while only an approved Implementer receives
edit-worktree mode. The adapter still repeats containment and clean-worktree checks.

A clean lease is removed after success, cancellation, failure, or lease-loss cleanup. A dirty lease is
never force-deleted: it is moved into `allowedRoot/quarantine` and remains available for review. A
small local lease sidecar records the session owner and state; on worker startup, stale managed leases
are reclaimed. Clean abandoned leases are removed, and dirty ones are quarantined. This prevents clean
orphan accumulation while preserving meaningful implementation work. Do not manually delete a
quarantine until its diff has been reviewed.

The canonical source checkout must remain clean and current. If it is not, the worker records a
bounded `WorktreeNotClean` preflight failure and does not launch Claude. A mapping without
`projectPath` retains the legacy static read-only behavior; supply `projectPath` for automatic
per-session isolation and for any edit-worktree mapping.

## Diagnosing a failed session

A failed worker session now carries a structured diagnostic: adapter category, adapter exit code,
whether the provider was launched, provider exit code when reported, and a canonical bounded message.
The task detail/MCP read shows this under the failed session, and the task reason distinguishes adapter
preflight from provider execution. Raw stderr, prompts, output, credentials, and filesystem paths are
not stored or returned. For example, a dirty edit workspace is reported as: `Implementer could not
start: WorktreeNotClean (adapter exit 5). Provider was not launched.`

## Reading a plan before approving it

If a task is waiting for approval after a Planner session, call Sakura's
`get_session_handoff_plan` with the `decisionId` from `open_decisions`, or open
`/handoffs/{handoffId}/plan` on the Demiplane. Both return the complete Planner artifact in bounded
pages.

Page with `offset` set to the previous response's `nextOffset` until `hasMore` is false. You hold the
entire plan when a response reports `isWholeArtifactRetrieved: true` — do not judge completeness by
whether the text reads like it ends.

The `completeness` field says what you were given:

| Value | Meaning |
| --- | --- |
| `Complete` | The whole plan, in one response |
| `Page` | Part of a stored whole plan — keep paging |
| `PartiallyRetained` | The plan exceeded the 200,000-character retention bound; the missing characters cannot be fetched |
| `Excerpt` | Only a bounded excerpt was ever stored — there is no remainder to page to |

`Excerpt` is expected for any session captured before complete plan retention existed (anything at or
before commit `d671d84`). Those plans were cut to 12,000 characters at the adapter and the rest was
never stored anywhere, so it cannot be recovered. Treat such a plan as a summary and not as the
artifact you are approving. See ADR-0020.

The artifact contract requires Goal and outcome, Scope, Concrete changes, Architecture and approach,
Risks and migrations, Non-goals, and Acceptance and verification.
