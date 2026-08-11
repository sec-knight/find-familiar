# ADR-0021: A worker that maintains the host, not a repository

- Status: Proposed
- Date: 2026-08-11
- Amends: ADR-0007 (Claude Code worker adapter), whose two modes both answered "what may this session
  change?" with a directory, and whose tool lists both excluded Bash on purpose
- Related: ADR-0006 (provider-neutral runner bridge), ADR-0010 (human-gated role handoff)

## Context

Every mode the adapter had described a filesystem boundary. Read-only meant "you may look at this
directory"; edit-worktree meant "you may change this disposable copy of it". Both are the right
answer for work whose subject is a repository, and the platform has been used for nothing else.

There is a second kind of work this deployment actually needs and could not express at all: work
whose subject is the machine. A worker stops heartbeating. A service needs restarting. A disk's
health needs reading before it fails. None of that is a file in a checkout, and no arrangement of
`Edit` and `Write` performs any of it.

Attempting it under the existing modes does not merely fail — it fails dishonestly. A session with
`Read,Grep,Glob` pointed at `/srv/familiar` can read a log and then must either say it cannot act or
invent an outcome, and ADR-0007 already records what a read-only session does when asked for
something it cannot reach: it produces a confident answer untethered from reality, and the platform
writes that into durable context.

So the choice was not between a safe capability and a dangerous one. It was between a capability
stated plainly and a capability the system pretends not to have.

## Decision

### 1. A third mode, named for what it does

`local-maintenance` joins `read-only` and `edit-worktree` in worker configuration and in the adapter.
It grants `Bash` alongside the file tools and runs as the worker's own OS user.

Its name does not describe a directory because it does not have one to describe. Once a session can
run commands, no path containment binds it: `--add-dir` does not constrain a subprocess, and a
worktree's cleanliness says nothing about whether a unit came back up. The mode therefore states the
boundary it actually has — the worker's user account — rather than a narrower one that would be false.

`allowedRoot` for such a mapping is expected to be `/`, and that is the honest value. A narrower root
would not restrain the session; it would only mislead the next person to read the config file.

### 2. Pre-approval by allow-list, never by bypass

The mode emits `--allowedTools Bash Edit Write` with `--permission-mode acceptEdits`. It does not
emit `--dangerously-skip-permissions` or `--permission-mode=bypassPermissions`, and
`ClaudeArgumentBuilder.ProhibitedFlags` remains true of every mode the adapter can produce —
the unit test asserting that now covers this mode too.

The distinction is not cosmetic. An allow-list grants exactly the tools named and leaves the
permission mechanism intact for everything else, including tools a future runtime version introduces.
A bypass disables the mechanism itself, permanently and for everything, and a mode that shipped with
one would silently absorb every capability added to the runtime thereafter.

### 3. What bounds it instead of containment

Four things, each of which survives the session having a shell:

- **The mapping must ask for it twice.** `mode: "local-maintenance"` alone does not load; the same
  mapping must also carry `acknowledgeHostAccess: true`. A configuration file copied from another
  host does not silently bring shell access with it.
- **Only the Implementer acts.** `ResolveMode` narrows by role exactly as edit-worktree does. A
  Planner planning a repair and a Reviewer reading the result do not need to run commands, so
  neither can.
- **A separate worker process.** The maintenance mapping lives in its own worker with its own
  systemd unit, not as a second mapping on the repository worker. Stopping that unit is a complete
  and obvious off switch, and an editing mistake in one JSON file cannot hand host access to the
  worker that builds the product.
- **A human still approves the plan.** Nothing reaches this worker that a person did not agree to,
  which is the same gate every other session passes through (ADR-0010).

### 4. No worktree, and no server change

`SessionWorkspaceLifecycle` returns the configured directory directly for a maintenance mapping and
creates nothing. The ephemeral-worktree path would otherwise build and delete session directories
directly beneath `allowedRoot`, which on this mapping is the root of the host.

The server learns nothing new. Mode remains worker-local (ADR-0006): no schema change, no migration,
no new field crossing the bridge. What the server sees is an ordinary project with ordinary sessions,
and the machine it happens to be about is entirely the worker's business.

### 5. The prompt envelope is rewritten, not reused

The repository envelope tells a session its scope is one directory and that it must not deploy or
execute anything. Handing that text to a session that is about to restart a service states rules the
session is required to break, and an envelope with one false rule in it teaches the model to read the
rest as advisory.

The maintenance envelope states rules that are true and enforceable here: do only the assignment's
work, diagnose before changing, prefer the reversible action, stop and report before a destructive
step rather than taking it, never weaken the machine's security posture, verify what you restarted,
and report what actually happened including failed attempts.

## Consequences

A session in this mode can damage this machine. That is inherent in the capability, not a gap in it,
and it is why the mode is opt-in twice over, role-limited, isolated in its own process, and gated on
a human approving a plan. The controls are administrative because the technical ones do not exist at
this altitude; saying so plainly is the point of this record.

This is a proof of concept on a demonstration lab. Before this pattern is used anywhere with real
data or real users, at least the following need answering, and none of them are answered here:

- the worker runs as `wizard`, a user with broad access to this box; a dedicated account with a
  targeted sudoers policy would bound it far better than the prompt does;
- there is no audit trail of executed commands beyond the session result the model itself writes,
  which is the wrong party to rely on for that record;
- destructive-step refusal is a prompt instruction, which is a real control on a cooperative model
  and no control at all against a prompt-injected one — and maintenance sessions read logs, which is
  untrusted input from anything that can write to them.

## Alternatives considered

**Widen the existing edit-worktree mode with Bash.** One flag, and it would have given every
repository session shell access on the build host — including Planners and Reviewers, on a worker
whose whole safety story is a disposable worktree. The two capabilities differ in kind and should
not share a mode.

**A dedicated non-Claude maintenance adapter.** Cleaner in principle: a fixed catalogue of permitted
operations (restart *these* units, read *this* SMART data) with no general shell at all, which is
the design that actually deserves production use. It is also a different and much larger project,
and it cannot troubleshoot the unanticipated — which is the entire use case here. Worth revisiting
when the set of maintenance tasks stops surprising us.

**Run maintenance by hand and paste results into the Familiar.** The status quo. It works, and it
loses exactly what the platform exists to keep: the durable link between what was decided, what was
done, and what happened.
