# File mentions (`@path`)

Type `@` in the prompt to attach a file from the current workspace. The CLI completes the path
for you, and when you send the message it reads the file and hands the content to the model as a
labelled attachment instead of leaving a bare `@path` string for the model to guess at.

## Using the picker

1. Type `@` at the start of a word. A picker opens under the prompt.
2. Keep typing to filter. Matching is fuzzy, so `@prog` finds `src/Andy.Cli/Program.cs` and
   `@feedview` finds `src/Andy.Cli/Widgets/FeedView.cs`.
3. Move the highlight with Up / Down (the list wraps).
4. Press Enter or Tab to accept. A file is inserted followed by a space; a directory is inserted
   with a trailing `/` and the picker stays open so you can drill in.
5. Press Escape to dismiss the picker for the current query and keep typing normally.

The picker only reacts when the cursor is inside the mention, so `sam@rivoli.ai` and a cursor
sitting elsewhere in a multiline prompt never open it. Accepting a suggestion replaces only the
mention token; the rest of a multiline prompt, and the caret's line, are left alone.

Files you have picked before are ranked ahead of equally good matches for the rest of the session.

## Syntax

| Form | Meaning |
| --- | --- |
| `@src/Foo.cs` | Attach the whole file |
| `@src\Foo.cs` | Same file - Windows separators are accepted |
| `@./src/Foo.cs` | Same file - a leading `./` is ignored |
| `@"docs/my notes.md"` | Quote paths that contain spaces, `"` or `#` |
| `@src/Foo.cs#L12-L40` | Attach lines 12 to 40 (one-based, inclusive) |
| `@src/Foo.cs#12-40` | Same as above |
| `@src/Foo.cs#L12` | Attach line 12 only |
| `@"docs/my notes.md"#L3-L9` | Quoted path with a range |

Notes on the `#` character:

- An unquoted `#` suffix is read as a line range only when it looks like one (`#12`, `#L12`,
  `#12-40`, `#L12-L40`). `@notes#draft.md` is treated as a file name.
- When both readings are possible, an existing file wins: if `notes#12` exists on disk,
  `@notes#12` attaches that file rather than line 12 of `notes`.
- Quote the path to remove all ambiguity: `@"docs/rev#12.md"`.

Without quotes a mention ends at the first space, so `@docs/my notes.md` looks for `docs/my`.

## What gets sent

The prompt text is sent exactly as you typed it, mentions included, followed by one block per
attachment:

```
<attached-file path="src/Foo.cs" lines="12-40">
...the requested lines...
</attached-file>
```

Mentions that could not be attached are still reported, so the model knows the file was requested
and why it is not there:

```
<attached-file path="secrets.env" status="ignored" note="The path is excluded by ignore rules and was not read." />
```

The transcript shows a `[files]` line summarising what was attached and what was skipped before
the model replies.

## Limits and refusals

Content is read **when you send the message**, not when you type the mention, so the model always
sees the file as it is at send time.

| Situation | Behaviour |
| --- | --- |
| File does not exist | `status="missing"`, nothing read |
| Path resolves outside the workspace root (`../`, an absolute path elsewhere) | `status="outside-workspace"`, nothing read |
| Path is excluded by `.gitignore`, `.git/info/exclude`, or the built-in skip list | `status="ignored"`, nothing read |
| File is not text (NUL bytes or mostly control bytes in the first 8 KiB) | `status="binary"`, nothing read |
| File is larger than 256 KiB | `status="too-large"` - add a line range to attach part of it |
| Path is a directory | `status="directory"` - mention a file inside it |
| Range starts past the end of the file | `status="range-out-of-bounds"` |
| Range ends past the end of the file | Clamped to the last line and attached |
| More than 20 mentions, or more than 1 MiB of content in one prompt | `status="budget-exceeded"` for the rest |
| The same path and range mentioned twice | Attached once; the repeat is silently skipped |
| The same path with two different ranges | Both attached |

The built-in skip list covers version-control metadata and build or dependency output:
`.git`, `.hg`, `.svn`, `node_modules`, `bower_components`, `vendor`, `bin`, `obj`, `dist`,
`build`, `out`, `target`, `.vs`, `.idea`, `.gradle`, `.tox`, `.venv`, `venv`, `__pycache__`,
`.mypy_cache`, `.pytest_cache`, `.next`, `.nuxt`, `.turbo`, `.parcel-cache`, `TestResults`,
`coverage`, `.andy`.

The picker lists at most 20,000 entries in very large repositories, and refreshes its listing a
few seconds after files change on disk.

## Privacy

Attaching a file sends its contents to whichever model provider the session is configured to use.
Treat every mention as an explicit decision to share that file.

- **Only files inside the workspace can be attached.** A mention that resolves outside the current
  working directory is refused, including absolute paths and `../` traversal.
- **Ignored files are refused, not just hidden.** If a path is excluded by `.gitignore`, typing it
  by hand does not attach it either. This is the main protection for files such as `.env`,
  credentials and local key material - but it only helps when those files are actually ignored.
  Check your ignore rules before attaching from a repository that keeps secrets in tracked files.
- **Attaching is never silent.** Every send prints a `[files]` line naming the files that were
  attached (with line ranges) and the ones that were skipped, so you can see what left the machine.
- **A line range limits what is shared.** `@config/settings.json#L1-L20` sends only those lines.
- **Nothing about your mentions is written to disk.** Recent-selection ranking is kept in memory
  for the session only and is discarded when the CLI exits.

## Not included in this slice

External Git reference aliases, MCP resources and agent mentions in the same menu, and image
attachments (tracked separately in #215) are out of scope.
