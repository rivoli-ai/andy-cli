# Headless build/test verification

Headless runs with changed .NET source or project files now run `dotnet build`
and `dotnet test` before publishing the final output. The gate detects content
changes, creations and deletions from an initial snapshot. It selects discovered
solutions, or projects when no solution exists. Unchanged .NET workspaces and
workspaces without .NET source changes do not trigger automatic verification.
The model's final prose is not verification evidence.

Commands run through `execute_command`, including the existing permission and
mode boundaries. Grant that tool in `permissions.allowed_tools` when builds are
intended. The gate does not grant it itself. Denial, missing process exit codes,
nonzero exit codes, cancellation and timeouts cannot satisfy verification.

For explicit test suites or other languages, configure commands. Explicit commands
always run, even if the source snapshot is unchanged:

```json
"verification": {
  "commands": ["dotnet build Project.sln --nologo", "dotnet test Project.sln --no-build --nologo"],
  "max_attempts": 2,
  "timeout_seconds": 120
}
```

Each failed attempt supplies bounded, redacted output and structured compiler
error/warning codes to a fresh correction agent restored from the current transcript.
Correction stays in the same run, with the original wall-clock deadline, remaining
global iteration budget and remaining Engine continuation time budget. The default
is two verifier attempts; the maximum is three. Exhausted verification exits 1;
exhausted iteration/time budgets retain exit 4. No final output file is published
while verification fails. `build_verification` events expose attempt, passed flag,
and per-command exit code, bounded output and diagnostics.

Automatic scanning excludes build artifacts, `.git`, `node_modules`, `.andy`, and
symbolic links. It is bounded to 10,000 filesystem entries, 8 MiB per source file,
64 MiB total source bytes and four solution/project targets. Larger repositories
should configure explicit commands, which bypass automatic scanning. Source files
outside the selected workspace and code returned only as response text are outside
the automatic detector. Configure explicit commands for custom build inputs and
non-.NET workflows. A zero exit code proves only the checks the chosen command runs;
it does not prove arbitrary external verifiers will pass.

## Completion - 2026-09-07

Added a permission-checked publication gate, same-run correction, structured and
redacted diagnostics, and a regression that compiles invalid C#, repairs it, and
only then publishes success (#156). In-process Roslyn analysis remains optional.
