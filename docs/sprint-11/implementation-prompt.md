# Sprint 11 — Implementer prompt

You are the Implementer for Sprint 11 of Find Familiar. Read
[`specification.md`](specification.md) first; it is authoritative, and this prompt does not restate its
reasoning.

---

## Your task

Make selecting a task on the Demiplane land the reader on that task's detail panel, on a phone, without
JavaScript.

## The change, precisely

### 1. `src/FindFamiliar.Server/Pages/Demiplane.cshtml`

Add `asp-fragment="task-detail"` to exactly two anchors:

- **line ~105**, the task link inside the `attention-item` list (Waiting for you)
- **line ~230**, the `task-node-title` link inside the `task-map` list (Work map)

Both currently read:

```cshtml
<a asp-page="/Demiplane" asp-route-id="@plane.ProjectId" asp-route-taskId="@task.TaskId">@task.Title</a>
```

They must render an href ending in `#task-detail`. Change nothing else in the file — not the section
order, not the detail panel, not the refresh block.

### 2. `src/FindFamiliar.Server/wwwroot/css/site.css`

Add one rule alongside the existing `.demiplane-detail` block (around line 702):

```css
#task-detail {
  scroll-margin-top: 1rem;
}
```

### 3. Tests — `tests/FindFamiliar.Server.Tests/Http/DemiplanePageTests.cs`

Add tests covering acceptance criteria 1, 2, 5, 6 and 9 from the specification. Follow the file's
existing conventions exactly: `[Fact]`, sentence-style method names with underscores, the
`SeedProjectAsync` / `SeedTaskAsync` helpers already in the file, `StringComparison.Ordinal` on HTML
assertions, and the shared `IntegrationTestCollection`.

The two negative tests matter as much as the positive ones. Assert that the Approve and Decline
redirects do **not** carry the fragment, and say why in a comment — they encode a decision the
specification makes deliberately, and without a test a later cleanup will "fix the inconsistency".

Add a rule asserting the CSS carries `scroll-margin-top` for `#task-detail` only if the test project
already has a precedent for asserting on static assets. If it does not, do not invent one; criterion 8
is then verified by reading the diff, and say so in your result.

---

## Hard boundaries

Do not:

- add JavaScript, or any script tag;
- add the fragment to the `RedirectToPage` calls in `Demiplane.cshtml.cs` — the specification explains
  why, and two tests will fail if you do;
- reorder, move or restructure any section of the page;
- change the projection, the display-state derivation, or any service;
- touch the approval fence, `SessionHandoff*`, or session lifecycle code;
- add a migration, change the model, or add an index;
- add a package reference;
- widen the change to the empty Work map heading, however tempting — it is explicitly deferred;
- commit, branch, push, or open a pull request.

If you believe the specification is wrong, **stop and say so in your result** rather than implementing
something else. A disagreement recorded is useful; a substituted change is not.

---

## Definition of done

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — all previously passing tests still pass, plus your new ones.
- `dotnet ef migrations has-pending-model-changes` — no pending model changes.
- The diff touches only `Pages/Demiplane.cshtml`, `wwwroot/css/site.css`, and
  `tests/.../DemiplanePageTests.cs`.
- No file outside `src/FindFamiliar.Server` and `tests/FindFamiliar.Server.Tests` is modified.

## What to report

State plainly, and separately:

1. The exact files and lines you changed.
2. The tests you added and what each asserts.
3. Build and test results, with the actual counts. If anything failed, say so and show the output.
4. Anything you could not verify, and why.
5. Anything you noticed but deliberately did not change.

Do not claim a manual phone check. You cannot perform one; the human does that from the acceptance
checklist.
