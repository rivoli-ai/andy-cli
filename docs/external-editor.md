# External editor (`/editor` and Ctrl+X)

Long specifications, pasted code and carefully structured prompts are easier to write
in your own editor. Andy can hand the prompt composer to that editor and take the
terminal back afterwards.

Implements issue #287.

## Using it

| Trigger | What it does |
| --- | --- |
| **Ctrl+X** | Opens your editor with whatever is currently in the composer. This is the binding to use. |
| **/editor** (alias **/edit**) | Same round trip, but submitting the slash command has already cleared the composer, so you start from an empty buffer. |

What happens:

1. Andy resolves your editor from `VISUAL`, then `EDITOR`.
2. The editable text of the composer is written to a private temporary file
   (`andy-prompt.md`, mode `0600`, inside a `0700` directory).
3. Raw keyboard mode, SGR mouse reporting, the alternate screen and cursor management
   are suspended, so the editor gets a normal terminal.
4. Your editor runs, directly - no shell is involved.
5. Andy restores the terminal, repaints the TUI, and deletes the temporary file.
6. **Only if the editor exited with status 0** is the composer replaced. A nonzero exit,
   a crash, a signal, a failed launch or an over-sized file all leave your prompt exactly
   as it was.

Press Enter afterwards to send the prompt as usual - Andy never submits for you.

## Configuring your editor

Andy reads `VISUAL` first and falls back to `EDITOR`. This is the POSIX convention:
`VISUAL` names the full-screen editor, `EDITOR` may be a line editor. A blank or
whitespace-only `VISUAL` is skipped so `EDITOR` still applies.

```sh
export VISUAL='vim'
```

Add the line to `~/.zshrc`, `~/.bashrc` or `~/.profile` to make it permanent. On Windows
PowerShell:

```powershell
$env:VISUAL = 'code --wait'
```

### Terminal editors

| Editor | Value | Notes |
| --- | --- | --- |
| Vim | `export VISUAL='vim'` | Blocks until `:wq`. No extra flags needed. |
| Neovim | `export VISUAL='nvim'` | Blocks until `:wq`. |
| Neovim, no user config | `export VISUAL='nvim -u NONE'` | Useful if a plugin misbehaves inside Andy. |
| Nano | `export VISUAL='nano'` | Save with Ctrl+O, exit with Ctrl+X. |
| Micro | `export VISUAL='micro'` | Save with Ctrl+S, exit with Ctrl+Q. |
| Helix | `export VISUAL='hx'` | Blocks until `:wq`. |
| Emacs (terminal) | `export VISUAL='emacs -nw'` | `-nw` keeps Emacs inside this terminal. |
| Emacsclient | `export VISUAL="emacsclient -nw -a ''"` | `-a ''` starts a daemon when none is running. |
| Kakoune | `export VISUAL='kak'` | Blocks until `:wq`. |
| Vi / ed | `export EDITOR='vi'` | Works, but prefer `VISUAL` for full-screen editors. |

### GUI editors - the `--wait` flag is mandatory

GUI editors normally hand the file to an already-running window and exit immediately.
Andy would then see a finished editor and read back an unedited file. Every GUI editor
therefore needs its blocking flag:

| Editor | Value |
| --- | --- |
| VS Code | `export VISUAL='code --wait'` |
| VS Code Insiders | `export VISUAL='code-insiders --wait'` |
| VSCodium | `export VISUAL='codium --wait'` |
| Cursor | `export VISUAL='cursor --wait'` |
| Zed | `export VISUAL='zed --wait'` |
| Sublime Text | `export VISUAL='subl --wait'` |
| BBEdit | `export VISUAL='bbedit --wait'` |
| Notepad++ (Windows) | `$env:VISUAL = '"C:\Program Files\Notepad++\notepad++.exe" -multiInst -notabbar -nosession -noPlugin'` |

If you forget `--wait`, Andy will look like it did nothing: the editor returns instantly
with status 0 and the file is unchanged.

## How the value is parsed

Andy launches the editor **directly**. There is no shell, so nothing in the value is
expanded. The value is split with a small, documented grammar:

- Tokens are separated by spaces and tabs.
- `'single quotes'` keep everything between them literally; there are no escapes inside.
- `"double quotes"` keep everything literally except `\"` and `\\`.
- Outside quotes, a backslash escapes the next character on macOS/Linux. On Windows a
  backslash is literal (it is a path separator), so quote paths there instead.
- An unterminated quote, or a value ending in a lone backslash, is an error and Andy
  tells you so instead of guessing.

**Not expanded** (passed through as literal argument text): `$VAR`, `~`, `*` and other
globs, `|`, `;`, `&&`, `>` and any other redirection. Shell pipelines cannot be used as
an editor.

### Paths and commands containing spaces

Quote the program path:

```sh
export VISUAL='"/Applications/My Editor.app/Contents/MacOS/edit" --wait'
```

Without the quotes, Andy would try to run a program literally named
`/Applications/My` and report a launch failure - leaving your prompt untouched.

Arguments with spaces work the same way:

```sh
export VISUAL="nvim -c 'set wrap linebreak'"
```

If you need a wrapper with shell logic, put the logic in a script and point `VISUAL`
at the script:

```sh
cat > ~/bin/andy-edit <<'SH'
#!/bin/sh
exec nvim -c 'set wrap linebreak' "$@"
SH
chmod +x ~/bin/andy-edit
export VISUAL="$HOME/bin/andy-edit"
```

Note that `$HOME` above is expanded by *your shell* when the variable is set, not by
Andy.

## Temporary file

- Created inside a fresh directory under the system temp directory
  (`andy-editor-<random>/andy-prompt.md`).
- Directory mode `0700`, file mode `0600` on macOS/Linux; on Windows the per-user temp
  directory ACL applies.
- The `.md` suffix gives editors sensible highlighting and soft wrapping for prose.
- Deleted on every completion path - success, nonzero exit, launch failure, cancellation
  and exceptions - plus a process-exit hook so an abrupt exit while the editor is open
  still cleans up.

## Limits and edge cases

- **Size limit.** A saved file larger than 1 MiB is rejected and the prompt is left
  unchanged, rather than being truncated.
- **Trailing newline.** Most editors terminate the last line unconditionally, so Andy
  drops exactly one trailing newline. A prompt you deliberately ended with a blank line
  keeps that blank line.
- **Empty prompt.** Deleting everything and saving is respected: the composer ends up
  empty. That is a successful edit, not a failure.
- **Unicode and newlines** are preserved verbatim; the file is UTF-8 without a BOM.
- **Line endings** are normalized to LF, matching the composer.
- **Ctrl+C / signals.** If the process is interrupted while the editor owns the terminal,
  Andy still restores line wrapping, the cursor and mouse mode, and stays out of the
  alternate screen it had already left.

## Structured `@file` and image parts

The composer is modelled as an ordered list of parts (`Andy.Cli.Editor.ComposerDocument`),
not a flat string. The editor only ever sees the *text* rendering: text parts verbatim
and each structured part as its placeholder (for an `@file` part, `@src/Program.cs`).

When the edited text comes back, every surviving placeholder is mapped back to the
**original part record** - kind, resolved reference and payload intact - at whatever
position it now occupies. Parts are never flattened into text. Deleting a placeholder
removes that attachment; moving it moves the attachment; duplicating it re-uses the same
attachment.

Today the composer on `main` is still a plain string, so a document has one text part
and no attachments. Issue #277 (structured `@file` prompt parts) only has to change
`Andy.Cli.Editor.PromptLineComposer` to build and consume real parts; the round trip and
the editor pipeline already handle them.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| "No external editor is configured" | Neither `VISUAL` nor `EDITOR` is set. See above. |
| Editor opens and Andy immediately says nothing changed | A GUI editor without `--wait`. |
| "could not start ...: No such file or directory" | The program is not on `PATH`, or a path with spaces was not quoted. |
| "could not be parsed: an opening double quote is never closed" | Unbalanced quoting in `VISUAL`/`EDITOR`. |
| "The editor exited with code N" | The editor reported failure; Andy deliberately keeps your original prompt. |
| "The editor was terminated by signal N" | The editor was killed; the prompt is kept. |
