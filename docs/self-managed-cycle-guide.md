# Running a Familiar-managed development cycle — operator guide

How to take one change to Find Familiar through Find Familiar's own workflow, and what you have to do
by hand because the application cannot yet do it.

This guide is honest about the second part. The point of a self-managed cycle is not to claim the
system is autonomous; it is to find out precisely where it is not.

---

## The one thing worth knowing

**Find Familiar coordinates the work. It does not run itself.**

It holds the task, proposes the next role, records your decisions, and tells the story afterwards.
Starting the server, running a worker, preparing a worktree, committing, and completing the task are
all yours. Everything below is written so you can tell the two apart at a glance.

---

## What the application does, and what you do

| Step | Who |
|---|---|
| Create the task | **Application** — Talk, or the project page |
| Record purpose and acceptance criteria | **Application** — task context entry |
| Start the first Planner session | **Application**, on your approval |
| Discover, claim and execute a session | **Application** — a worker you started |
| Capture the session result | **Application** — the worker posts it |
| Propose the next role | **Application** — a `SessionHandoff`, automatically |
| Approve or decline that role | **You**, through the Demiplane |
| Run the server | **You** |
| Configure and run a worker | **You** |
| Provide a clean linked git worktree | **You** |
| Review the resulting code | **You** |
| Commit, branch, push | **You** — the adapter cannot |
| Complete the task | **You**, always |

---

## Before you start

### 1. The runner bridge needs a token, on both sides

The server rejects every worker request when `RunnerBridge:Token` is unset, and committed
`appsettings.json` deliberately never carries a default. A development server started without it will
refuse workers — with no error on the Demiplane, because the Demiplane knows nothing about workers.

On the server:

```text
RunnerBridge__Token=<a-strong-random-value>
```

On the worker machine:

```text
FAMILIAR_RUNNER_TOKEN=<the same value>
```

Never commit either. See the [worker runtime guide](worker-runtime-guide.md).

### 2. A worker must declare every role you intend to approve

`capabilities` in `worker.json` lists the roles the worker accepts. For a full cycle that is all three:

```jsonc
"capabilities": ["Planner", "Implementer", "Reviewer"]
```

This matters more than it looks. **An approved step whose role no worker declares stays `Started` and
blocks its task** — a task may hold only one Started session, so nothing else can begin on it either.
The Demiplane names this state exactly: *"Waiting for a worker that can run Implementer."* If you see
it, add the role and restart the worker, or cancel the session.

### 3. The Implementer needs a clean **linked** worktree

This is the constraint most likely to stop you, and it is worth setting up before you begin.

For an Implementer session to change files, the project's mapping must be `edit-worktree`, and the
adapter requires a **clean linked git worktree** — created with `git worktree add`, not your primary
checkout. It refuses to start otherwise, so a run never mixes its changes into work in progress.

```text
git worktree add ../findfamiliar-sprint-11 -b feature/sprint-11-demiplane-selection
```

Then point the mapping at it:

```jsonc
"projects": [
  {
    "projectId": "<the Find Familiar project GUID>",
    "worktree": "/absolute/path/to/findfamiliar-sprint-11",
    "allowedRoot": "/absolute/path/to/parent",
    "mode": "edit-worktree"
  }
]
```

Opting the project in does not make every session a writing one — Planner and Reviewer stay read-only
regardless, because neither of their jobs is to change files.

**The adapter cannot commit or push.** `Bash` is excluded from its tool list, so there is no path to
`git` from inside the model's turn. You review the worktree afterwards and decide what to keep.

### 4. Back up the database if you care about it

Not required on a sandbox. Required anywhere else, before any sprint that migrates.

---

## The cycle

### Step 1 — Create the task

Either route creates a real task; they differ in what else they create.

**Talk** (`/Talk`) is the canonical intake. Describe the work, review the deterministic proposal, and
approve. Approving creates **one Ready task and one Started Planner session** together. Use this when
you are ready for the Planner to run.

**The project page** (`/Projects/Details`) creates a task and nothing else. Use this when you want the
task to exist before any session starts.

Nothing is created until you approve. No AI is called during intake — the proposal comes from fixed
rules, not a model.

### Step 2 — Record the acceptance criteria on the task

Open the task and record a durable context entry holding what "done" means. The assignment packet
renders active context entries, so this is how the criteria reach the Planner.

Do **not** try to pass instructions through the handoff. A handoff carries no free text, deliberately —
it is consent, not content. Guidance belongs in a context entry, which is provenance-tracked and
correctly advances the context revision.

### Step 3 — Let the Planner run

Start the worker. It polls, is granted one session at a time, and executes it through the adapter.

If nothing is claimed within a poll interval or two, check in this order: the worker process is
running; `FAMILIAR_RUNNER_TOKEN` matches the server's; the project GUID is in `projects`; the role is
in `capabilities`.

### Step 4 — Approve the proposed Implementer step

When the Planner's result is captured, the application stages a `SessionHandoff` proposing Implementer
and **stops**. A worker that can run Implementer will sit idle next to it indefinitely. That is the
design, not a stall.

Open the Demiplane. The task appears under **Waiting for you** with Approve and Decline. Read the plan
the Planner actually produced — from the task page, which shows it — then decide.

Approving from the Demiplane runs the same fenced transaction as approving from the task page. Nothing
else on that page starts anything, and refreshing it cannot approve.

### Step 5 — Let the Implementer work, then review the worktree

The Implementer changes files in the linked worktree. When it finishes, the application proposes a
Reviewer and waits for you again.

Before approving the Reviewer, look at the actual diff:

```text
git -C /absolute/path/to/findfamiliar-sprint-11 status
git -C /absolute/path/to/findfamiliar-sprint-11 diff
```

### Step 6 — Approve the Reviewer

Same gate, same page. When the Reviewer completes, the application proposes **nothing at all**. What
happens to a reviewed task is a decision about the task, and that stays with you.

### Step 7 — Review independently, then merge

The Reviewer session is a session, not an approval. Review the code yourself — or with a separate
review pass — before merging. Commit and push by hand; the adapter cannot.

### Step 8 — Complete the task

Mark it complete on the task page. Familiar never completes a task, on any path, on any timer.

---

## What the record will and will not show

Read this before you write the sprint's account, because the difference is the whole point.

**Persisted, and attributable to the application:** the task, its context entries, every session and
its status, every handoff and whether it was approved or declined, when consent was given, the worker
that claimed each session, and the captured result of each run.

**Not persisted anywhere:** that you started the server; that you configured a worker; that you created
the worktree; that you inspected the diff; that you committed. None of these leave a row, so none of
them may be described as something Find Familiar did.

**Never inferred:** build and test results. The project stores no structured build or test outcome, so
the Familiar reports "not recorded" rather than reading a claim out of a session's prose. If you want a
test result in the record, put it in a context entry yourself and say who ran it.

---

## Known gaps in the cycle

Found by inspection at the Sprint 10 baseline. None of them blocks a cycle; all of them are places the
system is less automated than a casual reading of the Demiplane would suggest.

- **Worker configuration is entirely out-of-band.** The server has no view of whether any worker
  exists, so a task can wait on a role no worker declares and the only signal is the reason text on
  the Demiplane. There is no "no workers are registered" warning anywhere.
- **The runner bridge token is a silent prerequisite.** An unset `RunnerBridge:Token` rejects every
  worker, and nothing in the UI says so.
- **Git is entirely manual.** Branch, worktree, commit, push and merge are all yours, by design
  (ADR-0010 lists them as non-goals). A completed Implementer session means files changed, not that
  anything was saved.
- **Build and test results are not part of the record.** See above.
- **Task completion is manual**, deliberately and permanently.

---

## Related

- [`sprint-11/specification.md`](sprint-11/specification.md) — the change this cycle carries
- [`demiplane-guide.md`](demiplane-guide.md) — the surface you approve from
- [`talk-workflow-guide.md`](talk-workflow-guide.md) — intake, the approval gate, and its guarantees
- [`worker-runtime-guide.md`](worker-runtime-guide.md) — worker configuration, capabilities and edit mode
- [`claude-worker-operator-guide.md`](claude-worker-operator-guide.md) — the adapter's own setup
- [`decisions/ADR-0010-human-gated-role-handoff.md`](decisions/ADR-0010-human-gated-role-handoff.md) —
  why every role needs its own approval
