# Conversational Familiar v0 — Reviewer Prompt

You are reviewing the Sprint 11 branch `feature/sprint-11-conversational-familiar-v0` against
[`specification.md`](specification.md), [`architecture.md`](architecture.md) and
[`user-experience.md`](user-experience.md).

Review the code, not the description of the code. Where a claim is made in a comment or a document,
find the line that makes it true.

---

## The question that decides the review

**Can text produced by a reasoning provider change persisted state without an explicit human
confirmation and a re-validation inside the confirming transaction?**

Trace it yourself, from `ClaudeFamiliarReasoningProvider.RespondAsync`'s return value forward, through
the validator, the proposal row, the page, the Confirm handler, and into `IWorkflowDispatchService`.
If there is any path that skips a step, the branch fails regardless of everything else.

## Blocking findings

Any one of these fails the review.

### Trust boundary

1. Provider output reaching a mutation without human confirmation.
2. A gate checked only at proposal time and not re-checked inside the confirming transaction.
3. A form field that lets a crafted POST choose the action kind, the project, or the target task.
   Only the proposal id, the token, and (for `CreateTask`) the human-edited title and outcome may be
   bound.
4. A tool declared on the provider call, or any execution surface reachable from model output.
5. A proposal from another project confirmable from this page.
6. Evidence ids accepted without checking they were in the snapshot that produced the message.

### Honesty

7. A sentence in the UI, the summary, or a wording table that the persisted data does not support.
8. Absence treated as an event — "you declined", "no worker exists", "the provider is out of quota" —
   where the data only shows a missing row. This is the specific failure ADR-0011 was written about.
9. A `DatabaseBusy` condition reported as a lost race, or a generic fault claiming a competing actor.
10. Any claim about provider capacity or quota. The shipped reader reports `Unknown` and must stay
    that way.
11. A failure message that speculates about a cause the server did not observe.

### Safety

12. A secret, host, header value, exception message, stack trace, or machine-local path reaching a
    page, a log, or the database. Grep the diff.
13. An API key read from configuration files rather than the environment.
14. Hidden reasoning, prompts, thinking blocks, or raw provider payloads persisted or logged.
15. `Html.Raw`, markdown-to-HTML, or autolinking applied to model text. A model-authored URL must not
    be clickable.
16. A `GET` that writes. Including conversation creation.
17. A state-changing handler without antiforgery, or reachable by `GET`.
18. Unbounded provider input — missing message length cap, unbounded history, or a snapshot sent while
    `IsWithinBudget` is false.

### Correctness and concurrency

19. Confirmation without a conditional consume of the `Pending` row by token. A check-then-write lets
    two contenders both pass inspection.
20. Effects committed across more than one transaction, or a partial commit possible on failure.
21. The user's message not durable before the provider call.
22. A filtered unique index whose filter no longer matches the stored enum representation, without a
    guard test.
23. `IX_AgentSessions_TaskId_Started` bypassed, or `StartPlanner` able to open a second session on a
    task.
24. Two definitions of a display rule — anything re-deriving task state instead of reading
    `IDemiplaneProjectionService`.
25. The Runner, adapter, claim path, or `Conversation`/`WorkProposal`/`SessionHandoff` behaviour
    changed.

### Tests

26. A required test from [`acceptance-checklist.md`](acceptance-checklist.md) missing, skipped, or
    weakened to assert something trivially true.
27. Any test that makes a network call or requires a credential.

## Non-blocking findings

Report, do not block: naming, comment density, wording that is accurate but graceless, missed reuse
that costs nothing today, ordering of Razor markup.

## What to verify by running

```
dotnet build                                                  # 0 warnings, 0 errors
dotnet test                                                   # all pass
dotnet ef migrations has-pending-model-changes                # clean
git diff --check main...HEAD                                  # clean
```

Then, by hand:

1. With **no** provider configured, open `/Familiar/{projectId}` on a project with several tasks in
   mixed states. The summary must be accurate against the database. Send a message: it is saved and a
   system note explains why there is no reply.
2. Diff the deterministic summary against `/Demiplane/{projectId}`. They must not disagree about any
   task's state; they read the same projection.
3. With the fake provider scripted to fail each way in turn, confirm each §7 wording appears and that
   nothing was written except the human message and the system note.
4. Script the fake provider to propose a `StartPlanner` on a task in a **different** project. No
   proposal may be created.
5. Script it to propose `CreateTask`, then advance the project's context revision, then reload. The
   Confirm control must be gone and the reason stated.
6. Confirm a `StartPlanner` proposal. Then verify from the database that the created session is
   indistinguishable from a manually started one: same shape, no conversational column, and the work
   queue, Demiplane and assignment endpoint treat it identically.
7. Confirm the same proposal twice by replaying the POST. Exactly one session exists and the second
   attempt says so truthfully.

## Questions to answer in the review

- Where, in one line of code, is a provider-proposed action prevented from executing itself?
- Which test would fail first if someone deleted the project filter from the snapshot builder?
- What does this page do for a user whose provider is down and whose project has 400 tasks?
- Is there anything in the database, after a week of use, that a user would be surprised to find
  stored?

## Verdict

**PASS** only with zero blocking findings. Record non-blocking findings for follow-up rather than
holding the branch. On acceptance, confirm ADR-0012 exists, is marked Accepted, and matches what the
code actually does — not what the plan said it would do.
