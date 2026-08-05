# Sprint 11 — Reviewer prompt

You are the Reviewer for Sprint 11 of Find Familiar. Read [`specification.md`](specification.md) and
[`implementation-prompt.md`](implementation-prompt.md) before the diff.

Your job is to decide whether the change is correct, minimal, and honest — and whether the sprint's
real deliverable, an inspectable lifecycle, actually held.

---

## Review the code

### Correctness

- Do both anchors render an href ending `#task-detail`, with the `taskId` query parameter intact?
- Does `<section id="task-detail">` still exist and still carry that exact id? A fragment pointing at
  a renamed element is a silent no-op.
- Is the `scroll-margin-top` rule present, and does it apply to the panel rather than to a wrapper?
- Does the page still render correctly with **no** task selected, and with a `taskId` belonging to
  another project?

### Minimality

The specification asks for three edits. Anything beyond them is a finding, including improvements you
agree with. Check specifically that the diff does not:

- add JavaScript;
- add the fragment to the two `RedirectToPage` calls in `Demiplane.cshtml.cs`;
- reorder or move any section;
- touch any service, projection, domain type, or migration;
- fix the empty Work map heading.

### The deliberate exclusions are still guarded

Confirm tests exist asserting the Approve and Decline redirects carry **no** fragment, and that they
explain why. If they are missing, that is a finding even though the behaviour is currently correct:
the specification's reasoning survives only if a test carries it.

### Tests

- Do the new tests actually fail against the unmodified page? A test asserting `Contains("#task-detail")`
  against a response that would contain it for another reason proves nothing. Check the assertions are
  specific to the anchors under test.
- Do they follow the file's existing conventions?
- Did the previously passing suite stay green, at its previous count plus the additions?

### The human visual check

The whole point of this change is that a person can see something they could not see before, and no
automated test asserts that. Section B of [`acceptance-checklist.md`](acceptance-checklist.md) is the
only evidence that the defect is actually fixed rather than merely addressed in markup.

**You cannot perform this check either** — you have no phone and no viewport. Your job is to confirm it
was performed by a human and recorded, and to say so plainly if it was not. A sprint whose section B is
blank has passing tests and no evidence the user's problem is solved; report that as blocking.

### Honesty

- Does the Implementer's report distinguish what it verified from what it did not?
- Does it claim any manual or visual verification? It cannot perform one. Treat such a claim as a
  finding in its own right.

---

## Review the cycle

This is the part that is specific to Sprint 11. The code change is small on purpose; the sprint's claim
is about the workflow that produced it.

Answer each, from the persisted record rather than from narrative:

1. Does the task in Find Familiar carry a purpose and acceptance criteria that match this
   specification?
2. Is the session chain on the Work map an accurate account of what actually ran — no session recorded
   that did not happen, none missing that did?
3. Was every role transition preceded by a recorded human approval, with a `SessionHandoff` row to
   show for it?
4. Does the task detail let a reader who was not present understand what happened, without opening a
   terminal?
5. Where a step was performed by a human outside the application, is that visible — or does the record
   imply Find Familiar did it?

Question 5 is the one that matters most. The failure mode this sprint is exposed to is not a bug; it is
a record that overstates the system's autonomy. If any step was done by hand and the persisted state
does not show it, say so plainly and treat it as blocking.

---

## Verdict

Report:

- **PASS** or **FAIL**, with blocking findings separated from non-blocking observations.
- For each finding: file and line, what is wrong, and what would make it right.
- Any workflow gap you found — a point where the cycle could not proceed through the application —
  whether or not it blocked this sprint.
- Anything you could not verify, stated as unverified rather than assumed.

Do not approve merges, commit, push, or complete the task. Completing a task is a human decision,
always.
