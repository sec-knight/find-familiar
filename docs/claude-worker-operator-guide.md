# Claude worker operator guide

How to point a worker machine at a Familiar server and run one explicit session through the
locally installed Claude Code CLI. See ADR-0007 for why each boundary exists.

For a worker that discovers and claims sessions automatically instead of being invoked per session,
see the worker runtime guide (`docs/worker-runtime-guide.md`) and ADR-0008. This guide remains
accurate for explicit invocation, and the adapter configuration below applies to both modes.

Every value below is a placeholder. Never commit real tokens, credentials, machine paths, or
tailnet identifiers.

## Prerequisites

- Claude Code installed and already authenticated locally (`claude --version` works, and a trivial
  prompt succeeds without prompting for login).
- The absolute path to the **native** Claude executable. Do not use `claude.cmd`, `claude.ps1`, or
  any other shim. No shell is ever used, so a shim will simply fail to launch.
- The Familiar server reachable from this machine over your private tailnet only.
- A checkout of this repository, built with `dotnet build FindFamiliar.slnx`.

## Configuration

The runner and the adapter are configured separately. The runner decides *which session*; the
adapter decides *what Claude is allowed to touch*.

### Runner variables

```text
FAMILIAR_RUNNER_TOKEN          the Familiar runner bridge credential
FAMILIAR_RUNNER_ADAPTER_PATH   absolute path to FindFamiliar.Adapter.Claude.exe
FAMILIAR_RUNNER_TIMEOUT_SECONDS  optional, default 300
```

Set the runner timeout **longer** than `FAMILIAR_CLAUDE_TIMEOUT_SECONDS` (default 600). If the
runner's timeout fires first it kills the adapter before the adapter can time out cleanly, kill
Claude's process tree, and report a precise category.

Leave `FAMILIAR_RUNNER_ADAPTER_ARGS` unset — this adapter takes no arguments and rejects any.

Set the token only in the current process or an OS secret store. Never place it in a URL, a
committed file, a shell profile that is backed up, or a report.

### Adapter variables

```text
FAMILIAR_CLAUDE_RUNTIME_PATH     absolute path to the native claude executable
FAMILIAR_CLAUDE_ENTRYPOINT       optional; only for a node + JS entrypoint installation
FAMILIAR_CLAUDE_WORKTREE         absolute path to the exact repository/worktree Claude may use
FAMILIAR_CLAUDE_ALLOWED_ROOT     absolute path to the directory the worktree must sit under
FAMILIAR_CLAUDE_MODE             read-only | edit-worktree
FAMILIAR_CLAUDE_TIMEOUT_SECONDS  optional, default 600, clamped to [5, 3600]
FAMILIAR_CLAUDE_EXTRA_ARGS       optional JSON array, e.g. ["--model","<model-name>"]
```

`FAMILIAR_CLAUDE_EXTRA_ARGS` must be a JSON array — a space-separated string is rejected, because
whitespace-splitting a Windows path is exactly the bug this design avoids. Permission-bypass flags
are rejected even here.

Example (PowerShell, placeholders only):

```powershell
$env:FAMILIAR_CLAUDE_RUNTIME_PATH = "<absolute-path-to-claude-executable>"
$env:FAMILIAR_CLAUDE_WORKTREE     = "<absolute-path-to-worktree>"
$env:FAMILIAR_CLAUDE_ALLOWED_ROOT = "<absolute-path-to-parent-of-worktree>"
$env:FAMILIAR_CLAUDE_MODE         = "read-only"
```

## Running one session

1. In Familiar, create or open the task and **start** exactly one session (Planner or Reviewer for
   a read-only run). Copy its task ID and session ID.
2. Set the variables above in the current shell.
3. Run the runner:

```text
dotnet FindFamiliar.Runner.dll \
  --base-url <tailnet-https-url> \
  --task-id <guid> \
  --session-id <guid>
```

4. Confirm in Familiar: four context entries, one terminal session, one revision increment.
5. For a read-only run, confirm the worktree is unchanged (`git status` clean, HEAD unmoved).

## Permission modes

- **`read-only`** — Claude gets no tools at all; it can reason about content supplied in the
  prompt. Use this for the first proof and for any Planner or Reviewer session.
- **`edit-worktree`** — Claude gets file editing tools but no shell, and the adapter refuses to
  start unless the target is the root of a **clean, linked, disposable worktree** created with
  `git worktree add`. A primary checkout is rejected even when clean, as is a subdirectory of one.
  Inspect the diff yourself afterwards. Claude cannot commit or push; do not do it on its behalf
  without reviewing.

Automatic pickup can also run `edit-worktree`, for approved Implementer sessions only, when a project
mapping opts in. The rules above are unchanged and still enforced by the adapter — the worker chooses
which repository and which mode, the adapter decides whether that is allowed. See the
[worker runtime guide](worker-runtime-guide.md).

## Exit codes

`0` success. Non-zero values are stable categories: configuration invalid, invocation invalid,
worktree rejected, worktree not clean, runtime launch failed, runtime timeout, runtime non-zero
exit, runtime output invalid, permission denial reported. Diagnostics on stderr are fixed
non-secret strings — they never contain prompts, model output, credentials, or paths.

## Troubleshooting

- **Worktree rejected** — the worktree is not inside the allowed root, one of the paths is
  relative or UNC, or a symlink resolves outside the root. Both paths must be absolute.
- **Configuration invalid** — a required variable is missing, a path does not exist, or extra
  arguments are not a JSON array.
- **Worktree not clean** — edit mode only. Either the target has uncommitted changes, or it is not
  the root of a linked worktree. Create one with `git worktree add <path>` and point at that.
- **Runtime launch failed** — the runtime path is wrong, or it points at a shim rather than the
  native executable.
- **Runtime output invalid** — the installed CLI version may have changed its flags or output
  envelope. Re-run discovery against the installed version before adjusting anything.

## Safety notes

- The Familiar runner token is removed from the adapter's child environment, so it never reaches
  Claude.
- Claude's own credentials stay on this machine and are never sent to Familiar.
- Never commit local configuration, tokens, worktree paths, or session output.

## Failure diagnostics

The adapter emits a fixed machine-readable failure envelope in addition to its human stderr line.
The runner forwards only bounded category and exit metadata to Familiar. It never forwards provider
stderr, prompts, transcripts, credentials, or paths. Exit `5` is `WorktreeNotClean`: the adapter
returns before starting Claude, and the durable task detail says that the provider was not launched.
A provider runtime failure includes the provider launch state and provider exit code when available.

When automatic pickup is enabled, do not repair a dirty session directory in place. The worker owns
per-session detached worktrees and quarantines dirty ones after the run. Inspect the quarantine diff,
then perform any cleanup deliberately.
