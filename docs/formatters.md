# Formatters and the Post-Mutation Pipeline

Issue: rivoli-ai/andy-cli#283

Andy can run a repository's own formatters immediately after it writes a file, and it computes the
diff it shows you from the file's **final on-disk bytes**. Without this, a file Andy wrote could
violate the project's formatting rules, and a formatter running later (by hand, or by a pre-commit
hook) would make the diff you were shown differ from what is actually on disk.

## What runs, and when

Every single-file mutating tool goes through **one** shared post-mutation pipeline:

| Operation | Tool | Target parameter |
|-----------|------|------------------|
| write / create | `write_file` | `file_path` |
| patch | `edit_file` | `file_path` |
| replace | `replace_text` | `target_path` |
| rename | `move_file` | `destination_path` |

The pipeline's order is fixed, and it is the point of the feature:

```
tool mutation succeeds
     |
[100] formatters run, in deterministic order
     |
re-read the FINAL on-disk bytes
     |
[200] snapshot finalization      (integration seam, issue #276)
     |
[300] LSP notification           (integration seam, issue #282)
     |
[400] diff computed from the final bytes, then rendered
```

Because the diff is computed last, what you see in the feed is exactly what the file contains.

Only the file that was just changed is formatted. The pipeline never walks a directory or a glob;
a broader reformat is a separate, explicit action.

## Configuration

Formatter definitions live in a JSON file, in two layers:

- project: `<project root>/.andy/formatters.json`
- user: `~/.andy/formatters.json`

Precedence, lowest to highest: **locally detected defaults < user < project**. Merging is by
formatter name, and a higher layer replaces the definition wholesale, so a partial override can
never silently inherit a command you did not write.

```json
{
  "formatters": {
    "csharpier": {
      "command": "csharpier",
      "arguments": ["format", "$FILE"],
      "extensions": [".cs"],
      "workingDirectory": null,
      "timeoutSeconds": 60,
      "enabled": true,
      "order": 10
    }
  }
}
```

| Field | Meaning |
|-------|---------|
| `command` | Executable to run. Required. Never installed by Andy. |
| `arguments` | Argument vector. `$FILE` is replaced with the file's absolute path; when no argument mentions it, the path is appended last. |
| `extensions` | Extensions this formatter handles, with or without the leading dot, matched case-insensitively. A definition with no extensions never matches. |
| `workingDirectory` | Where to run. Relative paths resolve against the session working directory. |
| `timeoutSeconds` | Wall-clock limit for one run, clamped to 1..600 (default 30). |
| `enabled` | Set to `false` to switch a formatter off, including a detected default. |
| `order` | Run order, ascending. Ties break on name, ordinal. Default 100. |

### Locally detected defaults

A small set of well-known formatters (`dotnet format`, `gofmt`, `rustfmt`, `black`, `prettier`) is
recognised automatically, but **only when the command is already installed**. Andy never installs,
downloads, or otherwise acquires a formatter. A command that does not resolve on `PATH` is reported
as skipped, with the reason.

Any of them can be overridden or switched off by defining the same name in the user or project
layer.

## Deterministic ordering

When several formatters match one file they run in ascending `order`, ties broken by name using
ordinal comparison. The sequence never depends on dictionary or filesystem enumeration order, which
matters because formatters are not generally commutative.

## Permissions, cancellation, and audit

A formatter is an arbitrary local binary, so it goes through the same consent path as any other
command Andy runs, **before the process is started**:

- The formatter's command line is authorized through the registered `IToolPermissionGate` as an
  `execute_command` action, so an ordinary `deny` rule on `execute_command` denies the formatter,
  and an `ask` prompts through the usual modal.
- Prompt decisions are recorded in the session approvals file like any other command, so formatter
  runs are auditable.
- Plan mode (issue #278) denies at this same gate, so it denies formatters automatically with no
  formatter-specific policy.
- The turn's cancellation token governs the formatter process: cancelling a turn kills it.

Denial is reported, never silently swallowed: the file is left unformatted and the agent is told so.

## Failure reporting

OpenCode logs formatter failures and moves on, which can leave the agent believing a file was
formatted when it was not. Andy instead returns the formatter's **exit code and bounded, redacted
stderr** with the tool result, under the `formatter_diagnostics` key (in the result metadata, in
dictionary-shaped result data, and appended to the result message).

Handled failure modes, each of which is reported rather than ignored:

| Outcome | Meaning |
|---------|---------|
| `CommandNotFound` | The binary could not be launched. |
| `NonZeroExit` | The formatter exited nonzero; the code and stderr are reported. |
| `TimedOut` | The formatter exceeded its timeout and was killed. |
| `Cancelled` | The turn was cancelled and the process was killed. |
| `PermissionDenied` | The gate refused before the process started. |
| `TargetMissing` | The formatter deleted the file it was asked to format. |
| `TargetEscaped` | The target was replaced by a directory, or by a link pointing elsewhere. |

`TargetMissing`, `TargetEscaped`, and `Cancelled` stop the remaining formatters for that file:
once the target is gone or has been swapped out, running the next formatter would act on something
other than the file Andy wrote.

### Bounded and redacted

Formatter output can echo environment variables, config files, or the contents of the file being
formatted, any of which may carry a token. Every diagnostic is passed through the session redactor
(bearer tokens, `key=value` secrets, provider API-key shapes, and the literal values of
secret-looking environment variables) and then truncated - per formatter and again for the combined
report - so a formatter that floods stderr cannot flood the model's context.

Capture itself is bounded at the source: the process runner stops accumulating each stream at a
fixed cap rather than buffering everything and trimming afterwards.

## `/formatters`

```
/formatters status <file>   Explain which formatters match that file, and why
/formatters <file>          Same as 'status <file>'
/formatters list            List every configured or detected formatter, with its state
/formatters path            Show the configuration file locations
/formatters help            Usage
```

`status` answers the two questions users actually ask when formatting did not happen - "is it
configured?" and "is the tool installed?" - by naming, for each formatter: whether the extension
matched, which config layer defined it, whether the command resolved and to what, its run order,
and the exact command line that would be executed.

## Code map

| File | Role |
|------|------|
| `Services/Formatting/FormatterDefinition.cs` | One formatter's configuration, extension matching, argument resolution. |
| `Services/Formatting/FormatterConfigLoader.cs` | Loads and merges the user/project layers and the detected defaults. |
| `Services/Formatting/FormatterAvailability.cs` | PATH resolution. Never installs anything. |
| `Services/Formatting/FormatterCatalog.cs` | Deterministic selection, plus the "why" behind each match. |
| `Services/Formatting/FormatterPermissionGate.cs` | Routes execution through the command-permission gate. |
| `Services/Formatting/FormatterProcessRunner.cs` | Launches the process with bounded capture, timeout, and kill-on-cancel. |
| `Services/Formatting/FormatterTargetGuard.cs` | Detects a formatter that deletes or escapes the target. |
| `Services/Formatting/FormatterRunner.cs` | Applies the matching formatters to one file, in order. |
| `Services/Formatting/FormatterDiagnostics.cs` | Redaction and bounding of everything reported. |
| `Services/Formatting/PostMutationPipeline.cs` | The shared pipeline and its ordering contract. |
| `Services/Formatting/PostMutationPipelineFactory.cs` | Composition; picks up registered steps from DI. |
| `Services/UiUpdatingToolExecutor.PostMutation.cs` | The executor's entry into the pipeline. |
| `Commands/FormattersCommand.cs` | `/formatters`. |

## Integration seams

The pipeline reserves two ordered slots for work that lands on other branches. Both are filled by
registering an `IPostMutationStep` in DI with the matching `Order`; nothing in the formatter code
needs to change.

- **`PostMutationStepOrder.SnapshotFinalize` (200)** - issue #276, snapshot transaction boundaries.
  A step here finalizes the session snapshot with the post-format bytes.
- **`PostMutationStepOrder.LspNotify` (300)** - issue #282, LSP notification ordering. A step here
  notifies the language server about the post-format bytes, exactly once.

A third seam is the configuration source: `FormatterConfigLoader.LoadLayer` is the minimal local
config reader that issue #280 (unified configuration) will replace. Everything downstream consumes
`FormatterDefinition` and does not care where it came from; the merge semantics (project over user
over detected, keyed by name) are the contract to preserve.

## Related documentation

- [Tool Execution Architecture](./tool-execution-architecture.md)
