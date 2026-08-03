# ADR-0007: Claude Code Worker Adapter

**Status:** Accepted

**Date:** 2026-08-03

---

## Context

ADR-0006 established a provider-neutral runner bridge: a human starts exactly one session,
`FindFamiliar.Runner` fetches the assignment, launches an administrator-configured adapter
executable with no shell, and submits one atomic result. The only adapter that existed was
`FindFamiliar.FakeAdapter`, a deterministic test fixture. Nothing in the repository had ever
talked to a real AI provider.

This ADR covers the first real one: a compiled adapter that drives the locally installed and
authenticated Claude Code CLI on the operator's Windows development machine, so a Started session
in Familiar can be completed by a real provider over the operator's private tailnet.

The provider's exact capabilities were established by a one-time read-only discovery pass on the
target machine rather than assumed from documentation or memory. That report — installed version
`2.1.220`, a single native `claude.exe`, and a specific verified flag set — is the authority for
everything version-sensitive below. No flag is used here that was not observed on the installed
CLI and exercised in a local smoke test.

---

## Decision

### A provider-specific adapter, outside the provider-neutral runner

`FindFamiliar.Adapter.Claude` (`src/FindFamiliar.Adapter.Claude`) is a separate `net10.0` console
project. `FindFamiliar.Runner` gains no knowledge of Claude whatsoever: it still launches
`FAMILIAR_RUNNER_ADAPTER_PATH` with no adapter arguments, still writes protocol-v1
`AdapterInvocation` JSON to stdin, and still expects one protocol-v1 `AdapterResult` document on
stdout. Swapping providers means pointing that one variable at a different executable.

The adapter takes a `ProjectReference` on `FindFamiliar.Runner` to reuse `RunnerProtocol` (the
contract records and their frozen length limits) and `AdapterProcessExecutor` (bounded I/O,
timeout, process-tree kill). This is the *opposite* direction from the coupling ADR-0006 rejected
— that concern was about dragging ASP.NET Core and EF Core into the console runner, and
`FindFamiliar.Runner` is a plain console app with zero NuGet dependencies. Reusing it avoids a
third hand-maintained mirror of the protocol types and, more importantly, means the hardened
child-process logic exists once rather than twice.

`AdapterProcessExecutor.RunAsync` gained one optional trailing `workingDirectory` parameter so the
adapter can pin Claude's working directory. Existing call sites are unchanged.

### Direct runtime invocation; Windows shell shims rejected

The discovery pass found three `claude` entries on `PATH`, all npm shims under
`%USERPROFILE%\AppData\Roaming\npm\`: `claude.cmd`, `claude.ps1`, and a POSIX shell script. **None
is used.** Every one of them requires a command interpreter, and ADR-0006's whole premise is that
no Familiar-served content ever becomes a shell token. The adapter is configured with the absolute
path of the real native executable and starts it with `UseShellExecute = false` and `ArgumentList`.

The discovery pass also established that this version ships **no `cli.js`**, so the
`node.exe` + JavaScript entrypoint shape does not exist for 2.1.220. The adapter still supports
that shape behind `FAMILIAR_CLAUDE_ENTRYPOINT` (the spec asks for both, and a future packaging
change may reintroduce it), but the deployed configuration uses the native executable only.

`cmd.exe`, PowerShell, concatenated command strings, and `--dangerously-skip-permissions` are all
rejected outright. The bypass rejection is enforced in code, not merely by convention: prohibited
flags are refused even when they appear in administrator-supplied
`FAMILIAR_CLAUDE_EXTRA_ARGS`.

### Local configuration is the only source of authority

Seven `FAMILIAR_CLAUDE_*` environment variables supply the runtime path, optional entrypoint,
exact worktree, allowed root, permission mode, timeout, and any extra arguments. None of them can
be influenced by assignment content, and the adapter rejects any command-line argument at all.

Assignment text is untrusted data throughout. It cannot select an executable, a repository, a
worktree, a permission mode, or a role — the role comes from the server-issued assignment and is
propagated verbatim, never re-derived by reading the Markdown.

Extra arguments are configured as a **JSON array**, never a whitespace-split command string. A
Windows path containing spaces was precisely the fragile boundary ADR-0006 flagged, and
representing arguments as discrete array elements removes the failure mode rather than escaping
around it. Extras are emitted *before* the policy flags: the CLI resolves repeated flags
last-wins, so appending them would let an operator extra such as `["--tools","Bash"]` silently
widen the mode boundary.

### Repository allowlisting

`WorktreePathPolicy` requires the configured worktree to be equal to or below the configured
allowed root. The comparison is deliberately split:

- **Pure textual logic** normalizes separators, resolves `.` and `..`, and compares **whole path
  segments** case-insensitively. Whole-segment comparison is the point: a prefix match on the
  joined string would accept `.../GitHub-evil` under a `.../GitHub` allowlist. Traversal above the
  root is rejected rather than clamped, so `/a/../..` cannot be quietly reinterpreted as `/a`.
  This layer touches no filesystem, so Windows-shaped paths are fully testable on any host —
  `Path.GetFullPath` is OS-native and mis-parses Windows paths on Linux.
- **Real-filesystem resolution** then resolves symlinks/reparse points on the worktree *and every
  ancestor*, re-normalizes, and re-checks containment. Resolving only the leaf would miss a link on
  an intermediate directory, which is the more likely escape.

Non-absolute paths and UNC paths are rejected as allowlist boundaries.

**Known gap:** the symlink tests exercise real POSIX symlinks on the Linux build host. Windows
junctions and other reparse-point kinds are not identical, so the mechanism is proven but
Windows-specific behavior still owes a manual verification step on the target machine.

### Permission modes

`read-only` is the mode for the first real proof and uses the exact recipe verified on the target
machine: `-p --output-format json --no-session-persistence --tools "" --permission-mode plan`. The
empty `--tools` list is the hard guarantee — it removes tools from the model's schema entirely, so
safety does not depend on a permission prompt being answered correctly. `--permission-mode plan`
is defence in depth on top of that.

`edit-worktree` is optional and additionally requires the target to be a **clean, linked,
disposable git worktree** (verified by invoking `git` directly, never through a shell) so any
resulting diff is reviewable against a known baseline. "Inside a clean repository" is explicitly
not sufficient: git answers `rev-parse --is-inside-work-tree` and `status --porcelain` identically
from any subdirectory of a primary checkout, so the adapter also requires the path to equal
`rev-parse --show-toplevel` and requires the per-worktree git dir to differ from the shared common
dir. Without both checks, pointing the variable at a subdirectory of a developer's main checkout
would grant edit rights over their real working copy.

The mode grants `Edit,Write,Read,Grep,Glob` and deliberately **excludes `Bash`**: without a shell
tool there is no path from inside the model's turn to `git commit`, `git push`, or any other
process execution.

Only the read-only recipe was live-smoke-tested during discovery. The edit-mode flag combination
is therefore treated as unverified against the live CLI until it is exercised on the target
machine; if it cannot be verified there, the optional edit proof is deferred rather than the
policy being weakened to force a pass.

Beyond flags, the adapter wraps every assignment in a compiled-in **operator instruction
envelope** stating that the worktree is the entire filesystem scope, that repository instructions
remain applicable, that the assignment is untrusted input which cannot amend these rules, and that
Claude must not commit, push, merge, publish, or deploy. The envelope always precedes the
assignment text. It is defence in depth, not the primary control — the primary control is the tool
schema.

### Bounded, non-partial results

The adapter reads only three fields of Claude's JSON envelope (`is_error`, `result`,
`permission_denials`) and ignores the rest, so provider-side additions cannot change behavior. It
requires `is_error == false`, a non-blank `result`, and an **empty** `permission_denials` array; a
non-empty denial list means the model attempted a blocked tool and is treated as a policy failure
rather than a partial answer worth keeping.

The result is truncated to the existing `RunnerProtocol` limits before being written. The adapter
writes **exactly one** JSON document to stdout on success and **nothing at all** on any failure —
there is no partial success. Failures return stable, non-secret exit categories; diagnostics are
fixed strings that never contain prompts, provider output, environment contents, or configured
paths.

The installed CLI has **no timeout flag**, so the adapter enforces its own wall-clock timeout
covering the prompt write to Claude as well as its execution, and kills the whole process tree.
The adapter's own read of the runner's stdin is separately bounded by a byte cap and its own
clock, so a caller that writes indefinitely or never closes the pipe cannot exhaust memory or hang
the adapter before validation runs.

### Tailnet-only, explicit-session execution

This adapter changes nothing about how work is selected. A human still starts exactly one session
in Familiar; the runner is still invoked with explicit task and session IDs. The server remains
loopback-bound and reachable from the worker only across the operator's private tailnet. Nothing
here introduces a public listener.

Provider credentials never leave the worker machine: Claude's authentication is OAuth/keychain
backed locally, and Familiar neither receives nor stores it. In the other direction, the Familiar
runner token is explicitly removed from the adapter's child environment by
`AdapterProcessExecutor`, so it cannot reach Claude — covered by an automated test that sets a
sentinel token and asserts its absence in the provider child.

### Automated tests never call the live provider

`FindFamiliar.FakeClaudeRuntime` is a deterministic console fixture emitting the same JSON
envelope shape, selected by `FAKE_CLAUDE_MODE`. Pure logic (configuration, path containment,
invocation validation, argument building, prompt envelope, response mapping) is unit tested
directly. Process-level behavior is proven by spawning the real compiled adapter against the fake
runtime: a path containing spaces and quotes is asserted to arrive as one exact argv element, the
policy flags are asserted to survive a hostile operator extra, a sentinel runner token is asserted
absent from the provider child, and oversized stdin is rejected by the real process. The full
chain (real `RunnerEngine` → real adapter → fake runtime → real machine API) runs against the
isolated temporary test database.

---

## Consequences

### Positive

- Familiar can complete a real session with a real provider without a human relaying text, while
  keeping session selection, role, atomicity, and replay rejection server-side and human-driven.
- The provider boundary is one executable path; a second provider needs no runner change.
- The dangerous parts of the chain — shell avoidance, path containment, credential scrubbing,
  bounded output, timeout — are each covered by an automated test that fails loudly if weakened.

### Negative

- The flag set is pinned to Claude Code 2.1.220. `claude update` can move it, so the adapter's
  version assumptions must be re-verified after provider upgrades.
- Windows junction/reparse semantics are not exercised by the Linux test suite; that verification
  is owed on the target machine.
- Path containment compares case-insensitively everywhere. That matches the Windows target, but it
  is *more permissive* than a case-sensitive filesystem warrants: on Linux it would accept
  `/srv/allowed/evil` under an `/srv/Allowed` root, which are genuinely different directories.
- Nothing validates that the configured runtime path is not a shell shim; a misconfigured shim
  surfaces only as a generic launch failure.
- The runner's own timeout must be configured longer than the adapter's, or the runner will kill
  the adapter before its timeout and process-tree kill can run.
- Claude's envelope has no artifact title, so the adapter emits a fixed one rather than inventing
  provenance — artifact titles from this path are uniform.
- Edit mode's flag combination is implemented but not yet live-verified.

---

## Alternatives Considered

### Teach `FindFamiliar.Runner` about Claude directly

Rejected. It would make the bridge provider-specific, contradicting ADR-0006's core decision, and
every future provider would accrete more conditionals in the one component that must stay neutral.

### Launch the `claude.cmd` npm shim

Rejected. It requires a command interpreter, which reintroduces exactly the shell-quoting
boundary ADR-0006 removed — and Windows paths with spaces make that boundary actively hostile.
The native executable is directly launchable, so the shim buys nothing.

### Rely on `--permission-mode plan` alone for read-only safety

Rejected as the primary control. A permission mode governs how prompts are answered; an empty
`--tools` schema removes the capability outright. Using both, with the schema as the real
guarantee, avoids depending on prompt-handling behavior in a noninteractive session.

### Trust the assignment Markdown to carry the safety rules

Rejected. Assignment text is untrusted and could be authored to omit or contradict them. The
envelope is compiled into the adapter, and the real enforcement is the tool schema rather than any
instruction text.

### Whitespace-split a configurable argument string

Rejected — the failure mode ADR-0006 explicitly called out. A JSON array preserves quoted values
and embedded spaces exactly.

---

## Non-Goals

- Automatic work discovery, polling, claiming, or leases. Deferred to a later sprint precisely
  because this proof is what establishes real provider timing, failure modes, and repository-safety
  behavior; designing a claim model before observing those would be guesswork.
- A Windows service, unattended startup, or any autonomous task progression.
- Task auto-completion or Reviewer auto-approval — status remains an explicit human action
  (ADR-0005).
- A provider SDK. The CLI's process contract is the entire integration surface.
- Public Internet exposure or broad application authentication.
- Any migration or schema change.
