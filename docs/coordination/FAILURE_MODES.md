# Coordination Failure Modes

Incidents that produced the rules in this system. Add to it. A rule whose incident is written
down is a rule the next agent understands instead of works around.

## Two agents in one checkout

A branch is not isolation. Two sessions sharing a working copy share `HEAD` and the index, so
agents with entirely disjoint file ownership still collide. In one incident an agent working in
the primary tree aborted another lane's in-progress merge. In another, an integration session and
a feature session both ran `git merge` in the same checkout, and the resolution that survived
did not compile.

Rule: every lane gets its own worktree, created by `coord start`, including integration.

## The ledger stalled the work it coordinated

Every lane appended a row to one coordination table. Two lanes adding unrelated rows is not a
disagreement, but git sees two edits at one anchor and calls it a conflict, and GitHub runs no
checks at all on a pull request that has a conflict. The result presented as "CI is broken", and
lanes spent their time diagnosing the wrong thing.

Rule: live coordination state is not a file in the repository.

## A closed issue had only half its work landed

An issue spanning two repositories was closed when the library half merged. The consuming half
was never integrated, and nothing noticed for weeks.

Rule: closing requires evidence verified against pushed integration state.

## Whole-tree staging published another lane's work

`git add -A` in a shared checkout committed files belonging to a different lane.

Rule: stage explicit paths.
