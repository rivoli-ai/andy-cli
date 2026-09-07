

<!-- coord:begin -->
# Development Coordination

How several agents and people change this repository at once without losing each other's work.

This describes coordination between the tools and people writing the software. It is not the
product's own collaboration design.

## Why this is a service and not a file

Both predecessors of this system kept live state under version control: a directory of claim
files in one repository, a board document in another. Both worked until they did not, in the same
way. Two agents editing a ledger is a merge conflict, GitHub runs no checks at all on a pull
request that has a conflict, and so a bookkeeping collision presents as "CI is broken" or "the
checks never appeared". Agents then spend hours diagnosing the wrong thing.

There is a second, quieter failure. A claim committed to a feature branch is invisible to anyone
starting from the default branch, so an agent can implement for hours behind a claim nobody can
see. Publishing every status change through a pull request is the only fix available to a
file-based ledger, and it is slower than the work it coordinates.

So live state moved out of git. What stays in the repository is the protocol, which changes
rarely, and `.coord/config.json`, which is this project's configuration. What lives in the
service is anything that changes by the minute: who holds what, which worktrees are live, what
the queue is. What lives on GitHub is the issues, which remain the durable record.

## The three sources of truth

| Subject | Authority | Why |
|---|---|---|
| Issues, pull requests, their state and content | GitHub | It outlives every local process and every agent session. |
| Claims, leases, worktree verdicts, the queue | the coord service | These change constantly and must be atomic. A file cannot be. |
| The protocol and this project's configuration | this repository | It is reviewed like code, because it is a contract. |

Coord never asks you to reconcile these by hand. Issue mutations are write-through: GitHub is
updated first, the local mirror second. A disagreement between them is a bug in the sync loop,
not a task for an agent.

## Claims

A claim is a row with a holder, a next action, a branch, a worktree and a lease. Taking one is
atomic: two agents calling `coord start` on the same issue at the same instant serialize, and
exactly one wins. The loser is told who won and what their next action is, which is the
information it needs to pick different work.

A lease expires if the holder stops reporting. Any `coord` call renews it, so an agent that is
working does not also have to remember to say it is working. An agent that dies releases its work
back to the queue with an event that says why, instead of holding it forever.

During long work, run `coord heartbeat` before the configured lease expires (30 minutes by
default). `coord update` also renews it. If it expires, run `coord start <target> --next "..."`
to resume your recorded lane. Coord checks its repository, branch and ownership before
re-taking the claim, preserving existing changes.

Ownership is by target, and additionally by branch and by worktree. Two lanes cannot share a
branch and cannot share a worktree, because both have already caused losses in this organisation.

## Worktrees

Every lane works in its own worktree on its own branch, created together by `coord start`.
There are no exceptions, including for integration: merging and closing issues are work and need
a worktree like anything else.

A branch is not isolation. Two sessions in one checkout share `HEAD` and the index, so agents
with entirely disjoint file ownership still collide there. This has happened: one agent working
in the primary tree aborted another lane's in-progress merge, and separately, an integration
session and a feature session both ran `git merge` in one checkout and the resolution that
survived did not compile.

A worktree isolates files and refs. It does not isolate the machine. Home directories, running
applications, preference domains, keychains and shared temporary paths are visible to every lane
at once. Treat each of them as owned by whoever claimed it.

Coord scans for worktrees continuously and gives each one a verdict:

| Verdict | Meaning |
|---|---|
| `primary` | The main checkout. No lane may work here. |
| `live` | Held by a current claim. |
| `orphan-clean` | No claim, clean, fully merged. An agent may remove it. |
| `orphan-dirty` | No claim, but uncommitted or unmerged work. Only a person decides. |
| `prunable` | Git records it, but the directory is gone. |
| `unregistered` | On disk, pointing at this repository, and git does not know it exists. |

That last row is why the scan looks at the disk rather than only asking git. A worktree whose
administrative entry was pruned while its files stayed behind looks like an ordinary source
directory to every other tool.

## The queue

`coord next` returns ranked work and does not return an empty list while any work exists. The
order is deliberate:

1. **open issues** that are unclaimed and unblocked;
2. **pull requests** with no reviewer, so reviews are picked up as work rather than as a favour;
3. **audits**, meaning anything closed or edited without recorded verification;
4. **sweeps**, meaning an area of the repository whose review cadence has elapsed;
5. **orphaned worktrees**.

Reviews, audits, sweeps and cleanup sit in the same ranked list as issues, below them. That is
the whole mechanism for "pick up a review when you have time" and for "review the repository on
your own initiative": both happen automatically when there is spare capacity, and neither
competes with shipping.

When even that is empty, the queue offers assistance work derived from real state: unblocking
another lane, verifying a recent closure, narrowing a blocker. Never invent work to appear busy,
and never duplicate an implementation somebody else is doing.

## Sweeps

A sweep is a periodic, self-directed review of one area: tests, documentation, defects, dead
code, dependencies. Areas and cadences are in `.coord/config.json`. When an area goes stale it
enters the queue, and whoever takes it files what they find with `coord file_issue`.

Filing is deduplicated against everything open and recently closed, so sweeping repeatedly does
not flood the backlog with the same finding under three different titles.

Closing the last filed issue is not the finish line. The issue list only holds problems somebody
already noticed, which is what sweeps exist to correct.

## Closing an issue

Closing requires the commit or pull request, the exact command run against pushed integration
state, and its result. Coord posts that evidence to the issue before the state changes, so the
record survives even if the close itself fails.

An assignee, a branch name, a stale claim, a local commit, an unmerged branch, or an "already
fixed" comment is not completion. Anything closed outside coord is detected on the next sync and
queued for audit, and an audit that fails reopens the issue.

## Handing off

A handoff carries: branch and commit, or an explicit statement that the work is an uncommitted
diff; the acceptance criteria completed; the surfaces claimed and the files actually changed; the
commands run and their results, including anything skipped; dependencies on other repositories
and the integration order they require; known risks and incomplete work.

Report disagreement with the brief, or an assumption you are unsure of, rather than resolving it
silently.
<!-- coord:end -->
