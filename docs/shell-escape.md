# Interactive shell escape

Updated: 2026-07-25

Shell escape lets you run a local shell command without leaving the TUI and without
asking the model to do it for you. Type `!` at the start of an empty prompt, type the
command, press Enter.

Implements rivoli-ai/andy-cli#286.

## Using it

| Action | Key |
| --- | --- |
| Enter shell mode | `!` at prompt offset zero, on an empty prompt |
| Run the command | `Enter` |
| Cancel the running command | `Ctrl+C` |
| Leave shell mode | `Escape` or `Backspace`, on an empty shell prompt |
| Send a command's output to the model | `/attach` |

In shell mode the prompt glyph changes from `>` to `!`, the prompt border turns the
theme's warning color, and an empty shell prompt shows the hint
`shell command - Esc to cancel`. Submitting keeps you in shell mode so you can run
several commands in a row; leave it explicitly with Escape or Backspace.

`!` is only a mode switch when the prompt is empty, the cursor is at offset zero, and
the keystroke is not part of a paste. Anywhere else - `really!`, or a pasted script
that begins with `!` - it is an ordinary character.

Commands run in the session's tracked working directory (the one shown in the header).
A standalone `cd` moves that directory for the rest of the session, exactly as it does
when the model runs one.

Each command's row in the feed shows the command, its output (stdout, with stderr in
the error color), the exit code when it is non-zero, the duration, and the word `you`.
That last part is the point: a user-invoked command is visually distinct from a
model-invoked one everywhere it appears.

## Security model

The short version: **shell escape is not a new privilege.** It is a faster way to
reach a capability andy-cli already has, and it is subject to exactly the same
controls.

### Everything goes through the permission evaluator

`UserShellCommandRunner` does not start a process. It dispatches the `execute_command`
tool through the `IToolExecutor` resolved from DI, which is the permission-decorated
executor installed by `AddAndyCliPermissions`. That is the single consent authority for
the whole application, so a command you type in shell mode is evaluated identically to
one the model requests:

- layered allow / ask / deny rules, resolved
  `Builtin < User < Project < Local < Injected < Session < Managed`;
- the built-in deny rules for destructive commands (`rm -rf /` and friends) block the
  command before any process starts;
- anything that resolves to *ask* raises the normal interactive approval prompt, with
  the same dangerous-command risk assessment (`ApprovalRiskAssessor`) and the same
  persisted approval scopes (`SessionApprovalStore`) as a model-invoked command - an
  "Allow (session)" grant you make for a `!` command is the same grant, recorded in the
  same place, and re-applied on resume;
- `/auto` (auto-approve) applies with the same carve-outs for high-risk actions.

Because consent is evaluated at that seam rather than around it, **any gate layered onto
the permission store applies to shell escape automatically**. In particular a Plan-mode
overlay that installs deny rules for mutating tools blocks a user-typed `!` command with
no change to the shell-escape code.

The one thing that must never be added to `UserShellCommandRunner` is a direct
`Process.Start`, or a call into the undecorated `Andy.Tools.Execution.ToolExecutor`.
Either would let a keystroke in the composer reach a child process without consent.

The capability flags the runner sets on the execution context
(`FileSystemAccess`, `NetworkAccess`, `ProcessExecution`, `EnvironmentAccess`) are **not**
a consent decision. They exist only so Andy.Tools' low-level capability check does not
reject `execute_command` before the permission gate has had its say - the same grant, for
the same reason, that `UiUpdatingToolExecutor` makes for model-invoked calls.

### The command string is passed to the shell verbatim

`ExecuteCommandTool` hands the command to the platform shell as a single argument
(`bash -c` on Unix, `cmd /c` on Windows). Quotes, pipes, redirects, globs, multi-line
input and non-ASCII text therefore follow the shell's own rules, and nothing in andy-cli
re-quotes or re-escapes them. That is deliberate: if the CLI rewrote the command, the
string shown in the approval prompt would not be the string that runs.

The working directory is passed as an explicit parameter rather than a `cd <dir> &&`
preamble, for the same reason.

Note one consequence of the packaged rule matcher: a command containing a redirection
operator is not decomposed into an allowable segment, so `echo x > file` prompts for
consent even when a broad `execute_command(*)` allow rule is present. This applies to
model-invoked commands identically.

### Cancellation and timeouts

`Ctrl+C` cancels the running command, not andy-cli.

The interactive TUI leaves `isig` enabled on the tty, so Ctrl+C is raised as SIGINT
rather than arriving as a byte on stdin. While a shell-mode command is in flight,
`InterruptDispatcher` claims that signal: the command's `CancellationTokenSource` is
cancelled, `ConsoleCancelEventArgs.Cancel` is set so the runtime does not terminate the
process, and the terminal is deliberately left in raw mode so the next frame paints
correctly. When no command is running the dispatcher is unarmed and Ctrl+C behaves
exactly as it always has.

Cancelling while an approval prompt is up resolves that prompt to deny-once, so the
composer is never left waiting on a dialog nobody can answer.

Each command also gets a wall-clock timeout (`ShellEscape:TimeoutSeconds`, default 120s).
A timed-out command is reported as `timed out`, distinct from a user cancellation.

### Output handling

- **Bounded.** Each of stdout and stderr is capped at `ShellEscape:MaxOutputCharacters`
  (default 40,000). The row reports how many characters were dropped.
- **Not sent to the model.** Command output is never added to the conversation
  automatically. `/attach` is the only path, it is always user-initiated, and it puts the
  text into your composer rather than sending it - so you see and can edit exactly what
  the model will receive. `/attach` with no argument lists the last ten commands;
  `/attach 1` picks the most recent.
- **Redacted where it leaves your terminal.** The feed shows output verbatim, because
  that is your own terminal and redacting it would make shell mode useless for inspecting
  your environment. Everything that *leaves* the terminal is scrubbed with
  `SessionRedactor` first: the persisted session log, and the text `/attach` inserts.
  Bearer tokens, `key=value` secrets, provider API-key shapes and the literal values of
  secret-looking environment variables are replaced with `[REDACTED]`.

### Auditing and attribution

Commands you run in shell mode are recorded in `~/.andy/sessions/<id>.shell.json`,
a file separate from the conversation transcript (`<id>.json`). Each record carries
`"kind": "user_shell"` and `"source": "user"`, plus the command, exit code, status,
duration and working directory, with a bounded redacted output preview.

They are kept out of the transcript on purpose:

- a user command can never be mistaken for a model tool call in replay or export,
  because the two never share a container;
- resuming a session restores the model's context exactly as it was, rather than
  retroactively teaching it what you did on the side;
- the engine's `TranscriptSnapshot` schema, which the CLI does not own, needs no change.

On resume the two are merged by timestamp for display only: shell commands appear as
`[user shell] ! <command>  (<status>)` entries, tagged `EntryKind.UserShell`, and are
never counted in the `[N tool calls executed]` notice.

## Disabling the feature

Shell escape can be switched off outright. Two independent sources can disable it, and
neither can be overridden by the other - they are ANDed - so an operator's managed
configuration cannot be undone by an environment variable in a user's shell profile.

### Configuration

```jsonc
// src/Andy.Cli/appsettings.json (or any configured provider)
{
  "ShellEscape": {
    "Enabled": false
  }
}
```

### Environment variable

```sh
export ANDY_SHELL_ESCAPE=0     # also: false, no, off, disabled
```

`ANDY_SHELL_ESCAPE=1` does **not** re-enable a feature that configuration has disabled.

When disabled, `!` is an ordinary character at the prompt, the composer can never enter
shell mode, no runner is constructed, and `/attach` reports that there is nothing to
attach.

### Other settings

| Key | Default | Meaning |
| --- | --- | --- |
| `ShellEscape:Enabled` | `true` | Whether `!` enters shell mode |
| `ShellEscape:TimeoutSeconds` | `120` | Wall-clock budget for one command |
| `ShellEscape:MaxOutputCharacters` | `40000` | Characters kept from each of stdout and stderr |

Out-of-range values fall back to the defaults rather than failing to start.

## Where the code lives

| Concern | File |
| --- | --- |
| Settings and the disable switch | `src/Andy.Cli/Configuration/ShellEscapeOptions.cs` |
| Execution through the permission gate | `src/Andy.Cli/Services/Shell/UserShellCommandRunner.cs` |
| Result model and redaction boundary | `src/Andy.Cli/Services/Shell/UserShellCommandResult.cs` |
| `/attach` buffer and prompt text | `src/Andy.Cli/Services/Shell/UserShellOutputAttachment.cs` |
| Per-session audit log | `src/Andy.Cli/Services/Sessions/UserShellLogStore.cs` |
| Replay attribution | `src/Andy.Cli/Services/Sessions/SessionReplayFormatter.cs` |
| Composer mode | `src/Andy.Cli/Widgets/PromptLine.cs` |
| Feed row and presenter | `src/Andy.Cli/Widgets/Tools/UserShellFeedRow.cs`, `UserShellPresenter.cs` |
| Ctrl+C routing | `src/Andy.Cli/Input/InterruptDispatcher.cs`, `RawTerminalInput.cs` |

## Related documentation

- [Tool execution architecture](tool-execution-architecture.md)
- [Commands and keyboard shortcuts](README_COMMANDS.md)
