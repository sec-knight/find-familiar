# ADR-0015: A relevance floor on retrieval, and an automated repository snapshot

- Status: Proposed
- Date: 2026-08-07
- Supersedes: none
- Related: ADR-0002 (canonical data and rendered context), ADR-0003 (session result capture),
  ADR-0013 (talk lane / do lane split), ADR-0014 (the conversation is the control surface)

## Context

Two problems, joined by one theme: **the Familiar answering confidently from something it should not
have been given.**

### The observed defect

Pressing "Plan this" returned an unrelated open item — "DEFECT: plans name absolute paths" — worded as
though it were a freshly drafted plan for the thing actually being discussed.

The planning path was not at fault. Planning from a fresh exchange works and renders Approve/Decline
correctly. The fault was one level down, in the retrieval that feeds it: the talk lane's search
returned its best-scoring candidate with no notion of whether that candidate was *good enough*, and
its best-scoring candidate shared exactly one word with the question — "plan", in a title, which is
the heaviest signal the scorer has. Everything downstream then behaved correctly on a bad input.

Ranking always has a best candidate. Relevance does not. A search with no floor cannot distinguish
"here is the answer" from "here is the least-unrelated thing in the store", and it presents both in
the same register. **A confidently-worded wrong answer is worse than an empty one**, because an empty
one is visibly empty and a wrong one is not.

### The repository the Familiar could not see

Separately, the Familiar has never known what the repository actually contains. It knows projects,
tasks, sessions and context entries; it does not know that `FamiliarContextRetrievalService.cs` exists
or which directory it lives in. The previous arrangement was that somebody occasionally pasted a file
listing into a conversation — which meant the picture was usually months stale, and nobody could tell
how stale, because a pasted listing carries no date.

An earlier approved plan proposed capturing this on sprint boundaries. That is the same failure with a
longer period: the ceremony is what people skip when they are busy, which is exactly when the
repository is moving fastest.

## Decision

### 1. Retrieval has a floor, and below it the answer is "nothing"

`FamiliarContextRetrievalService` applies a minimum relevance threshold. An entry that does not clear
it is not carried, whatever it ranked. When nothing clears it, the result is an explicit no-match and
the prompt says so.

**The floor is two numbers, because there are two ways to be irrelevant.**

- `MinimumScore` (default 4) rejects the *weak* match: one query term mentioned once in a long body.
- `MinimumMatchedTerms` (default 2) rejects the *narrow* match: a single term landing somewhere
  heavy — a title — while touching one word of a five-word question.

The second is the one that produced the defect, and no absolute score bar could have caught it. A
title hit is worth eight content hits precisely because a title is a statement of what an entry is
about; that weighting is correct, and it is also exactly what lets one accidental title hit clear any
score floor. Breadth is the dimension that separates them, so breadth is measured and bounded
separately.

`MinimumMatchedTerms` is clamped to the number of terms the question actually yielded. A one-word
question — a bare identifier, the most precise query a person can type here — must remain answerable,
and on it the score bar is the whole guard.

**Both live in configuration** (`Familiar:Retrieval`), not as literals. The right numbers are a
property of the corpus rather than of the algorithm, and this corpus is thirty-odd entries today.

**A near-miss is disclosed as a count, never as content.** The result carries how many entries shared
a word with the question and failed to clear the bar, and the prompt states that number with an
instruction not to guess at them. This is the same disclosure rule sensitivity already follows —
*what*, not *which* — applied to irrelevance. Saying nothing at all would be its own small dishonesty:
retrieval did surface something, and "nothing matches" in a store that visibly contains the words
asked about is misleading in a different direction.

Rejected: **normalising the score into a 0–1 relevance and thresholding that.** It reads better and
means less. The normaliser would have to divide by something — the best score in this result set, or
a theoretical maximum — and both make the bar depend on what else happens to be in the store, so the
same entry passes or fails the same question depending on its neighbours. Two absolute numbers a
person can reason about beat one derived number nobody can.

Rejected: **asking the model whether a result is relevant.** A second round trip, in the lane whose
whole point is latency, to ask something that cannot see the store the question that the search
already answered.

### 2. The repository snapshot is a worker task, an ordinary context entry, and there is exactly one

**Trigger: post-commit hook, or a timer. Never a sprint boundary.** The hook
(`deploy/git-hooks/post-commit`) makes a snapshot current within seconds of the repository moving. The
timer makes it current anyway on a machine where the hook was never installed, or was installed in one
worktree and not the three others, or was wiped by a fresh clone. A snapshot is also taken at startup.
Nothing in this depends on a person remembering to do anything, which is the entire requirement.

**Content comes from git, not the filesystem.** `git ls-files --full-name`, `git log -20 --oneline`,
and a two-level view derived from the tracked paths. The originally specified `find . -type f` with a
`.git` exclusion is wrong twice over: it is neither two-level nor filtered, so it reports `bin/`,
`obj/`, `node_modules` and SQLite files, and calls the build output of one machine the state of the
repository. Asking git means the answer already honours `.gitignore` and is identical on every
checkout.

The two-level view is derived in C# rather than by piping git through `cut` and `sort` — the same set,
one process instead of three, no shell in the chain at all, and an ordinal sort that does not change
with the host's locale. The header carries date, branch, HEAD sha, and the literal marker
`snapshot-supersedes-prior`.

**Supersession is delete-on-write.** The Worker deletes every prior snapshot row inside the same
transaction that inserts the new one. Exactly one exists at any moment.

Rejected: **retrieval-time filtering** — keep every snapshot, return the newest. This puts a
correctness requirement into every consumer, including consumers not yet written, and a consumer that
forgets it does not fail loudly; it answers confidently about a repository as it stood in March. A
rule that must be remembered to be correct is the same class of defect as a snapshot that must be
remembered to be taken.

**Storage is an ordinary context entry**: kind `Summary`, fixed title
`Repository state snapshot (current)`, no new table and no migration. It is retrieved through exactly
the path everything else is retrieved through, which is why the title is fixed and undated — the title
is the row's identity, both for delete-on-write and for a person searching the store. The date
describes the content, in the header, rather than naming it.

*Migration target:* if this outgrows a context entry — per-file metadata, history across commits, more
than one repository — the answer is a dedicated repo-state table with its own retrieval path, not a
larger entry. The signal to migrate is the ceiling below binding so hard that the snapshot is mostly
trim notes.

**A capture does not increment the project's context revision.** A revision bump means the evidence a
human is looking at moved underneath them, and it invalidates every pending proposal and plan that
observed the old value (`WorkApprovalService`, `FamiliarActionService`). An automated write every half
hour that did this would make plan approval fail permanently. A plan drafted against a snapshot half an
hour old is a far better outcome than a plan that cannot be approved at all.

**A capture that fails writes nothing.** git is read before anything is deleted, so an unreadable
repository — an index lock, a filesystem that has gone away, git not installed — leaves the previous
snapshot exactly as it was. A stale snapshot naming the commit it describes is still true about that
commit; an empty one is true about nothing.

### 3. The ceiling is 8 KB, and trimming is normal rather than exceptional

A snapshot of this repository is naturally three or four times the budget. A snapshot that does not
trim is one that silently becomes the largest thing in every prompt, so cutting is the ordinary case
and the interesting question is not *whether* it was cut but whether the reader can tell.

Every trimmed section states what was cut and by how much, in the reader's own units:
`[tracked files trimmed: 118 of 366 paths shown]`. That answers the only question that matters about a
truncated section — am I looking at the repository, or a corner of it — without anybody counting.

**Trim order: the exhaustive file list, then the two-level view, then the commit log.**

The specification for this slice said "the two-level view first, then the log", and this deviates by
inserting the file list ahead of both while preserving their relative order. The reason is that the
stated order cannot enforce the ceiling: the exhaustive list is 366 paths here, roughly twice the
entire budget on its own, so trimming the other two sections to nothing still leaves the snapshot over
8 KB. Some order that reaches the file list is required.

Given that, the file list goes first rather than last, because it is the section whose loss costs
least. The two-level view above it already states what is in the repository and roughly how much of
it; cutting that summary in order to preserve more raw paths would leave a reader holding a corner of
the repository with nothing to tell them it was a corner — the exact failure the trim notes exist to
prevent. The log is kept longest: twenty subject lines are the cheapest statement of what has recently
changed that this snapshot can make.

The ceiling itself is a constant rather than configuration. It is a bound on how much of every prompt
one automated entry may consume, and an operator raising it would be trading away the retrieval budget
of every other entry without seeing that trade.

## Consequences

- Retrieval returns less. Some questions that previously got a marginal entry now get an explicit
  "nothing recorded matches this", which is the intended trade and is visible in the prompt rather
  than silent.
- The floor is keyword-overlap-shaped, like the scorer under it. When the corpus outgrows keyword
  overlap, the floor is replaced along with the scoring, not tuned around.
- Citations pointing at a deleted snapshot row become unsupported and are dropped by the existing
  validation. That is correct: the snapshot they cited no longer exists.
- The server now runs `git` on the host when configured. Fixed literal commands, no shell, bounded by
  a timeout, and off unless a path is set.
- The trigger endpoint (`POST /api/repository/snapshot`) sits behind the runner bridge token — the
  same machine-to-machine credential the Runner uses, because a second credential to distribute would
  be a second credential to leak.
- One more automated writer of context entries, which is one more thing that can fill the store. It
  cannot: delete-on-write means the snapshot's footprint is exactly one row of at most 8 KB, forever.
