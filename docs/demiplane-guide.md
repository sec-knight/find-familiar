# The Demiplane — user and operator guide

The Demiplane is a project's command surface: one page that answers where the project is, what is
happening, what needs you, and what to do next. It is designed to be readable on a phone through
Tailscale, so you can check on work without opening a terminal.

Reach it from **Projects**, or directly at `/Demiplane/{project-id}`.

---

## The one thing worth knowing

**The Demiplane shows you the project; it never decides anything on its own.**

Approving a proposed step from this page runs exactly the same fenced transaction as approving it from
the task page (ADR-0010). Nothing on this page auto-starts work, and the page refreshing does not
trigger anything.

---

## What you see

### Observatory — where we are

A tally of every task by state. Only states with tasks appear, so a healthy project is a short line.

### Waiting for you — the decisions

Every task needing a human, with Approve and Decline where a step is proposed. This is the section to
read first; if it is empty, nothing is blocked on you.

### Scrying pool — provider readiness

What each provider can currently handle.

**Today this reads "Unknown" for Claude, and that is accurate rather than unfinished.** The Claude Code
CLI exposes no scriptable usage or quota surface, so remaining capacity cannot be read without
guessing. Familiar will not display a number nobody reported. See ADR-0011 for what a real reader
would need.

### Work map — what is happening

One node per task. Each shows its state, why, and its session chain — the roles that have run and the
one proposed next, drawn as a dashed, explicitly un-started step.

Tasks are listed independently because **this project records no dependencies between tasks**. The map
does not draw edges it cannot substantiate.

### Task detail

Select any task to read the Familiar's account: what happened, what is happening now, why it is
waiting, what needs you, and what it recommends. Links to the full task record, the assignment packet
and the raw context sit beneath it.

---

## Task states

| Marker | State | Meaning |
|---|---|---|
| ○ | Not started | No session has run |
| ◌ | Waiting | Waiting on something; the reason says what |
| ⟳ | Running | A session is executing now |
| ! | Needs attention | Waiting on a decision from you |
| ⏸ | Blocked | Cannot proceed without you |
| ✓ | Succeeded | You marked the task complete |
| ✕ | Failed | A session ended because something went wrong |
| ⊘ | Cancelled | Someone deliberately stopped it |

Every state is shown as a marker **and** a text label. Colour is a third, redundant signal, so the page
works in monochrome and for anyone who cannot distinguish the accents.

A state never appears without a reason.

---

## Reasons you may see

### "Waiting for an available Implementer."

Ordinary queueing. A worker declares that role and should claim it shortly. Nothing to do.

### "Waiting for a worker that can run Implementer."

**This one needs you.** No enabled worker declares that role, so the session cannot be claimed — and
because a task may hold only one running session, nothing else can start on that task either.

**What to do:** add the role to a worker's `capabilities` (see the worker runtime guide), or cancel the
session so the task is not stuck.

### "The worker lease expired without a result."

A worker stopped mid-run. The session becomes claimable again automatically. Nothing to do.

### "Waiting for your approval to start the Implementer session."

Sprint 09's gate. Approve or Decline.

### "A Reviewer finished. Completing this task is your decision."

Familiar never completes a task. Mark it complete, or record what still needs doing.

### "The provider request failed during the Implementer session."

The provider exited with an error. **A usage limit also appears here** — the adapter cannot yet tell
exhaustion apart from other provider errors, so this reason says so rather than guessing which it was.

### "The Implementer session exceeded its time limit and was stopped."

The run hit the adapter timeout. Consider whether the task is too large for one session.

---

## What the Familiar will not tell you

Deliberate omissions, so you know what silence means:

- **Build and test results.** This project stores no structured build or test outcome, so the summary
  says "not recorded" rather than reading a claim out of a session's prose.
- **Provider usage numbers.** See above.
- **Task dependencies.** None are recorded.
- **Why a task was marked blocked by hand.** The domain stores the status, not a reason.

Where the data cannot support a statement, the page says so. It does not fill the gap with something
plausible.

---

## Refresh behaviour

While any task is running or waiting, the page reloads every 30 seconds. When nothing is in flight it
does not reload at all, so reading a summary is never interrupted.

The reload is a plain page load. It starts no work, claims nothing, and cannot approve anything.

---

## Using it from a phone over Tailscale

The Demiplane is a responsive web page, not an app. There is nothing to install.

### One-time setup

1. Have Familiar running on a machine in your tailnet, bound so the tailnet can reach it.
2. Install Tailscale on the phone and sign in to the same tailnet.
3. Open the machine's tailnet address in the phone browser.

If you use `tailscale serve` to put Familiar behind HTTPS on the tailnet, the same URL works from
every device you own. Nothing is exposed publicly.

### What changes on a small screen

The work map becomes a single-column trail — a focused list top to bottom, not a shrunken canvas. The
task detail collapses to stacked labels and values. Approve and Decline become full-width buttons with
comfortable touch targets.

Same data, same states, same decisions. The metaphor is entering the same Demiplane through a smaller
portal.

### Add it to the home screen

On iOS, Share → Add to Home Screen gives you a launcher that opens straight into the project. It is
still the web page; there is no native application.

---

## Operator notes

### Nothing new to deploy

Sprint 10 adds no service, port, endpoint, credential, worker setting or schema change. No migration
is required. The runner contract version is unchanged at 1.

### Provider readers

The readiness strip is driven by `IProviderCapacityReader` implementations registered in `Program.cs`.
Each is bounded by a three-second timeout and isolated: a reader that throws or hangs becomes one
"Unavailable" card carrying a generic message, and the rest of the page renders normally. The
exception's own text is never shown, because a reader could put a path or a credential in it.

To add a real reader, implement the interface and register it. The rule that must not bend: a reader
that cannot determine a value returns `Unknown` rather than an estimate.

### Verifying the boundary yourself

The Demiplane performs exactly two mutations, both POST with antiforgery, both delegating to the
Sprint 09 approval service. To confirm it starts nothing on its own, open a project with a proposed
step and leave the page open:

```sql
SELECT Status, ProposedRole FROM SessionHandoffs WHERE TaskId = '<task>';
-- Pending, however long the page stays open and however often it refreshes

SELECT COUNT(*) FROM AgentSessions WHERE TaskId = '<task>' AND Status = 'Started';
-- 0
```

---

## Related

- `docs/decisions/ADR-0011-task-display-state-and-provider-capacity.md` — why display state is derived
  outside the view, why failure categories come only from our own strings, and why provider capacity
  is Unknown rather than estimated
- `docs/decisions/ADR-0010-human-gated-role-handoff.md` — the approval gate this page surfaces
- `docs/talk-workflow-guide.md` — starting work conversationally
- `docs/worker-runtime-guide.md` — worker capabilities, and the blocked-task consequence
