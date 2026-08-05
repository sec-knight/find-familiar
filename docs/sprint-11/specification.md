# Sprint 11 — First Familiar-Managed Development Cycle

**Status:** Specified, not started

**Date:** 2026-08-05

**Baseline:** `main` at `8062144`; accepted code baseline `de035ee`; tag `demiplane-baseline-v0`; 523 tests passing.

---

## Why this sprint exists

Sprint 10 gave Find Familiar a command surface. Sprint 11 asks a different question: can Find Familiar
coordinate a change to itself, through its own workflow, and can a human read the whole story afterwards
from the Demiplane alone?

The code change is deliberately small. It is the *carrier* for the real deliverable, which is an
inspectable lifecycle: a task exists, a role is proposed, a human approves, a worker claims, the work
happens, the outcome is captured, review is requested and completed, and the chain is legible on the
Work map. A large change would make the sprint about the change. A small one keeps it about the cycle.

This document specifies the code change. The lifecycle itself is described in
[the self-managed cycle guide](../self-managed-cycle-guide.md), which is the operator-facing half of
this sprint.

---

## The defect

**Selecting a task on the Demiplane does not take you to the task.**

The Demiplane is documented as a phone-first surface — the demiplane guide opens by saying it is
"designed to be readable on a phone through Tailscale, so you can check on work without opening a
terminal." Selecting a task is the page's primary interaction, and on a phone it silently fails.

### What actually happens

Selecting a task is a navigation, not a client-side toggle. Both entry points render a plain anchor to
the same page with a query parameter:

- `Pages/Demiplane.cshtml:105` — the task link in **Waiting for you**
- `Pages/Demiplane.cshtml:230` — the task title in the **Work map**

Both produce `/Demiplane/{projectId}?taskId={taskId}`. Neither carries a fragment. The application ships
no JavaScript at all — `wwwroot/js` does not exist, and the layout's only `<script>` is an empty
importmap — so nothing scrolls the document after load. The browser therefore lands at the top.

The detail panel, `<section id="task-detail">`, is the **last** section in the document
(`Pages/Demiplane.cshtml:262`). Everything below is what a reader must scroll past to reach it:

```
dashboard-intro
Observatory      — Where we are
Summoning circle — Waiting for you
Scrying pool     — Provider readiness
Work map         — map-note + one card per task
Task detail      — #task-detail          <- the thing that was selected
```

On a desktop the map is a multi-column grid (`grid-template-columns: repeat(auto-fill, minmax(19rem, 1fr))`,
`site.css:596`), so the map is short and the detail is often already on screen. On a phone the
`max-width: 48rem` rule collapses it to a single column (`site.css:740`), by design — "a trail, not a
shrunken canvas." That correct decision is what makes the defect bite: every task becomes a full-width
stacked card, so the detail panel is pushed one further screen down per task in the project.

The result is that tapping a task appears to do nothing. The page reloads, the scroll position is the
top, and the only visible change is a selection outline on a card the user may not even be able to see.

### Why this is a defect and not a preference

This is not "the layout could be nicer". It is the page's primary interaction producing no perceptible
feedback on its primary target device. The `is-selected` outline exists precisely to indicate selection,
and the reader cannot see it. The Demiplane's own guide promises the phone view is the "same data, same
states, same decisions" — but a decision you cannot navigate to is not a decision you can take.

---

## The change

Three edits. No schema change, no new dependency, no new service, no new endpoint.

### 1. The two selection anchors carry a fragment

Add `asp-fragment="task-detail"` to the anchors at `Demiplane.cshtml:105` and `Demiplane.cshtml:230`,
so both render `href="/Demiplane/{projectId}?taskId={taskId}#task-detail"`.

The target already exists and already has that id. This adds no element, no class and no state; it
tells the browser where the navigation was meant to land. It is the native mechanism for exactly this
problem, it needs no script, and it degrades to current behaviour anywhere the target is absent.

### 2. `#task-detail` gets scroll margin

The panel must not land flush against the top edge of the viewport, which reads as a truncated page.

```css
#task-detail {
  scroll-margin-top: 1rem;
}
```

Keep it with the `.demiplane-detail` rules in `site.css`. One property, no layout impact when the
fragment is not used.

### 3. Nothing else

In particular, see below for what is deliberately excluded.

---

## What is deliberately **not** changed

### The Approve and Decline redirects keep no fragment

`OnPostApproveAsync` and `OnPostDeclineAsync` (`Demiplane.cshtml.cs:62` and `:77`) return
`RedirectToPage(new { id, taskId = outcome.TaskId })`. It is tempting to add the fragment here too, for
symmetry.

**Do not.** The outcome message is rendered as `TempData["StatusMessage"]` near the top of the page
(`Demiplane.cshtml:38-41`). Scrolling the user to the detail panel after a decision would scroll them
straight past the confirmation that the decision was recorded. Landing at the top after acting is
correct: the user's question changes from "what is this task?" to "did that work?", and the answer to
the second question is at the top.

This is a decision, not an oversight, and the acceptance tests assert it so a later tidying pass cannot
quietly reverse it.

### The section order stays as it is

Moving the detail panel above the Work map would also solve the scroll problem, and is rejected. The
page's reading order is deliberate — health, then what needs you, then capacity, then everything, then
the one thing you asked about. A selected task is a focused read *after* the overview, and reordering
the document to serve navigation would degrade the page for the reader who scrolls it top to bottom,
which is how it is read when nothing is selected.

### No JavaScript

The application has none, the talk workflow guide lists "Works without JavaScript" as a guarantee, and
a scroll behaviour is not worth becoming the first script on the page. A fragment is the no-script
answer.

### The empty Work map heading is recorded, not fixed

With zero tasks, the Work map section renders its heading and nothing else — `Demiplane.cshtml:212`
guards the entire body on `plane.Tasks.Count > 0`, while the Observatory has an explicit empty-state
message and the Work map does not. Verified against the live page on the current database:

```html
<section class="demiplane-map" aria-labelledby="map-title">
    <div class="section-heading">
        <div><p class="eyebrow">Work map</p><h2 id="map-title">What is happening</h2></div>
    </div>
</section>
```

This is a real defect and it is in the sprint's preferred scope list ("improve empty-state clarity").
It is **not** in Sprint 11. It affects only a project with no tasks, which is a state a project leaves
once and never returns to, whereas the selection defect affects every phone interaction for the life of
the project. Bundling them would mean the cycle proved out two changes at once and neither cleanly.

Recorded here as the first candidate for the second self-managed cycle.

---

## Acceptance criteria

Each is verifiable by an automated test or a stated command. New tests belong in
`tests/FindFamiliar.Server.Tests/Http/DemiplanePageTests.cs`, which already covers this page through
the real HTTP pipeline.

| # | Criterion | How it is verified |
|---|---|---|
| 1 | The Work map task anchor targets the detail panel | HTTP GET a project with a task; the anchor href contains `?taskId={id}` **and** ends `#task-detail` |
| 2 | The Waiting-for-you task anchor targets the detail panel | Same, for a task with a pending handoff |
| 3 | The fragment target exists when a task is selected | GET with `?taskId=`; response contains `id="task-detail"` |
| 4 | Selecting a task still renders the Familiar's account | Existing test `Selecting_a_task_reveals_the_familiar_summary` still passes, unmodified |
| 5 | Approve does **not** redirect to the fragment | POST Approve; the `Location` header contains `taskId=` and does **not** contain `#task-detail` |
| 6 | Decline does **not** redirect to the fragment | POST Decline; same assertion |
| 7 | The status message remains reachable after a decision | The redirect target renders `demiplane-flash` above the map, unchanged |
| 8 | `#task-detail` has scroll margin | `site.css` contains a `#task-detail` rule setting `scroll-margin-top` |
| 9 | No behaviour changed for an unselected page | A project with tasks and no `taskId` renders no `#task-detail` and no error |
| 10 | Nothing else regressed | `dotnet test` — 523 existing tests pass, plus the new ones |
| 11 | No schema change | `dotnet ef migrations has-pending-model-changes` reports none |

### Visual verification (human, on a phone)

Automated tests cannot assert "the user can see it". One manual check, recorded in the acceptance
checklist:

1. Open the Demiplane on a phone (or a browser at a 390px-wide viewport) for a project with at least
   three tasks.
2. Tap a task in the Work map.
3. The task detail is on screen without scrolling, with a small gap above it.
4. Scrolling up from there still reaches the Observatory and Waiting for you.
5. Approve or decline a step; confirm the outcome message is visible on landing, at the top.

---

## Consequences

### Accepted

**The meta refresh will re-apply the fragment.** While work is in flight the page carries
`<meta http-equiv="refresh" content="30">`, which re-requests the current URL. A browser that preserves
the fragment across that reload will re-scroll to the detail panel every 30 seconds. For a reader who
is watching a selected task this is the right place to be returned to; for a reader who has scrolled up
to the Observatory it will pull them back down. This is a real cost and it is accepted rather than
hidden: the alternative is either no fragment (the defect) or a scroll-position script (the excluded
JavaScript). It is worth re-examining if the refresh becomes intrusive in practice.

**A `taskId` from another project produces an unresolved fragment.** `DemiplaneModel` selects the task
only from the current project's projection (`Demiplane.cshtml.cs:90-91`), so a foreign `taskId` renders
no detail panel and the fragment names an element that does not exist. Browsers ignore this and stay at
the top, which is the correct outcome. No error, no change to the existing test that covers it.

### What gets better

The Demiplane's primary interaction produces visible feedback on the device it was designed for.
Nothing about the page's guarantees moves: no new mutation, no new write, no change to what the page
claims, and the approval boundary is untouched.

---

## Non-goals

- A graph library, or any change to how the map is drawn.
- A native mobile application.
- New provider integrations, or any change to provider capacity reading.
- Conversational AI, or any AI-authored workflow decision.
- Schema changes, migrations, or new indexes.
- Any change to the approval fence, the handoff lifecycle, or session semantics.
- Moving the detail panel, or any reordering of the page's sections.
- JavaScript.
- Fixing the empty Work map heading (recorded above, deferred deliberately).
- The four open follow-ups carried from Sprint 10 — `StaleContext` on concurrent losers, cancellation
  reason lookup shadowing, SQLite classification predicates belonging in shared infrastructure, and
  legacy `NoNextStepProposed` noise. None of them blocks this cycle.

---

## Related

- [`../self-managed-cycle-guide.md`](../self-managed-cycle-guide.md) — running the cycle, and what the
  operator must do by hand
- [`implementation-prompt.md`](implementation-prompt.md) — the Implementer's brief
- [`reviewer-prompt.md`](reviewer-prompt.md) — the Reviewer's brief
- [`acceptance-checklist.md`](acceptance-checklist.md) — what must be true before acceptance
- [`../decisions/ADR-0011-task-display-state-and-provider-capacity.md`](../decisions/ADR-0011-task-display-state-and-provider-capacity.md)
  — why the page says only what the data supports
- [`../demiplane-guide.md`](../demiplane-guide.md) — the surface this sprint improves
