# Undo and redo (shadow Git snapshots)

`/undo` reverts the filesystem changes made by the most recent completed
interactive turn, and `/redo` reapplies the turn that `/undo` last reverted.
Both are transactional: a turn is either fully reversible or not recorded at all.

Implementation: `src/Andy.Cli/Services/Undo/` (`ShadowGitRepository`,
`UndoManager`, `GitProcess`) plus `src/Andy.Cli/Commands/UndoCommand.cs` and
`RedoCommand.cs`.

## How a turn becomes a transaction

1. Before the agent starts a turn, the workspace is snapshotted.
2. The turn runs. Tools create, modify, rename and delete files as usual.
3. When the turn completes, the workspace is snapshotted again and the two
   snapshots are diffed. The changed paths, both snapshot ids and the original
   prompt form one transaction on the undo stack.
4. `/undo` restores the pre-turn contents of exactly those paths. `/redo`
   restores the post-turn contents of the same paths.

A turn that changed no files records nothing. An interrupted, cancelled or
failed turn is aborted and records nothing, so a partially applied turn never
becomes an undoable transaction.

After a successful `/undo` the reverted prompt is placed back in the composer so
it can be edited and resent.

## Where snapshots live

Snapshots are stored in a shadow Git repository outside the workspace:

```
~/.andy/snapshots/<workspace-id>/
```

`<workspace-id>` is the workspace directory name plus a hash of its absolute
path, so two checkouts with the same name never share a store. The shadow
repository is driven with an explicit `GIT_DIR`, `GIT_INDEX_FILE` and
`GIT_WORK_TREE`, with user and system Git configuration disabled.

Each session owns a ref under `refs/andy/sessions/<session-id>` inside the
shadow repository; every snapshot is a commit chained onto that ref. When the
session ends the ref is deleted and the unreachable objects are pruned, so
snapshots do not accumulate across sessions. In-memory history is bounded to the
20 most recent transactions per session.

## Safety guarantees

- The user's Git index, refs, stash, branch, reflog and configuration are never
  read for writing or modified. Every git invocation targets the shadow
  `GIT_DIR` and its own index file.
- Restores never run a git command that writes into the working tree. File
  contents are read out of the shadow object database and written by the CLI,
  so only the paths inside the transaction are ever touched.
- Files that a turn did not change - including pre-existing dirty tracked files
  and untracked files - are preserved byte for byte.
- A restore is planned and staged in full before anything is applied. If any
  path cannot be restored (missing snapshot, unsupported entry such as a
  submodule, a directory now occupying a file path) the command refuses and
  applies nothing rather than performing a partial restore.
- `/undo` and `/redo` are refused while a turn is still running.
- Starting a new turn invalidates the redo history.
- If a turn cannot be snapshotted, the whole history is dropped rather than left
  in a state where a later undo would silently restore over unrecorded changes.

## Limitations of the first slice

- **Git only.** Snapshots are Git trees, so the workspace must be inside a Git
  repository. In a non-Git directory `/undo` reports that the workspace is not a
  Git repository and suggests running `git init`; nothing else changes.
- **Ignored files are outside every transaction.** Files matched by `.gitignore`
  (or the repository's exclude files) are not snapshotted, so `/undo` neither
  reverts nor deletes them. Build output and local secrets are therefore never
  disturbed, but a generated artifact created during a turn stays behind.
- **History is per session and in memory.** Resuming or restarting a session
  starts with an empty undo history, and the previous session's snapshots are
  pruned when it exits.
- **One workspace per session.** The workspace is fixed at startup; changing the
  working directory mid-session with `cd` does not move the snapshot scope.
- **Turns that change nothing are not recorded**, so `/undo` reverts the most
  recent turn that actually changed files.
- **Whole-workspace snapshots.** The pre/post images cover the whole workspace,
  so an edit made by the user (outside Andy) while a turn is running is included
  in that turn's diff and would be reverted with it.
- Submodules and gitlink entries are not restorable; a transaction touching one
  is refused instead of partially applied.

## Tests

`tests/Andy.Cli.Tests/Services/Undo/` covers dirty worktrees, untracked files,
ignored files, creation, deletion and rename, binary round-tripping, redo
invalidation after a new turn, interrupted turns, retention bounds, snapshot
cleanup (including leaving other sessions' snapshots intact), and the assertion
that the user's Git index, refs and stash are byte-for-byte unchanged.
`tests/Andy.Cli.Tests/Commands/UndoCommandTests.cs` covers the slash-command
surface.
