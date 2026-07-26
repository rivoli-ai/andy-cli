# Operating modes: Build and Plan

Issue: rivoli-ai/andy-cli#278

Andy has two primary operating modes. **Build** is the normal, full-capability session. **Plan** is
a strictly read-only research and planning session.

The single most important thing to understand about this feature:

> **Plan mode is a permission boundary, not a prompt.** The model is told about the mode, but what
> actually stops a write is a tool-permission overlay that runs before the permission engine. If the
> prompt were deleted tomorrow, Plan mode would still refuse every mutating tool call.

## Planning DATA vs permission ENFORCEMENT

These are two unrelated systems that both use the word "plan". Do not conflate them.

| | Planning data | Mode enforcement |
| --- | --- | --- |
| What it is | The structured plan Andy.Engine emits (steps, statuses, revisions) | The read-only tool policy attached to Plan mode |
| Where it lives | `src/Andy.Cli/Services/EnginePlanBridge.cs`, rendered by `FeedView` | `src/Andy.Cli/Modes/` |
| What it does | Shows the user what the agent intends to do | Decides whether a tool call is allowed to run at all |
| Security relevance | **None.** It is display state. | This is the boundary. |
| Can the model change it | Yes, that is its purpose | No. The model cannot switch modes. |

A session can be in Build mode and still display a plan. A session can be in Plan mode with no plan
data at all. Turning planning data on or off has no effect on what tools may run, and switching
modes does not create, edit, or delete any plan.

This first slice deliberately has **no writable plan file**: Plan mode is non-mutating, full stop.

## Using it

```
/mode              show the current mode and the available modes
/mode plan         switch to read-only planning
/mode build        switch back to full capability
```

Start-up and headless selection:

```
andy-cli --mode plan
andy-cli run --headless --config run.json --mode plan
```

While Plan mode is active a highlighted `PLAN` badge sits in the status line. Build mode shows no
badge - it is the unrestricted default, and the badge exists to make the restricted state
unmistakable (the same convention as the `AUTO` auto-approve badge).

## What Plan mode allows

Allowed: reading files, listing directories, searching text and files, code-index queries, git
inspection (`git_diff`, `git_log`, `git_status`, `git_show`, `git_blame`), host inspection, PDF
extraction, in-memory dataframe inspection and transformation, agent-skill reads, and `http_request`
with a safe method (`GET`, `HEAD`, `OPTIONS`).

Denied: every file write (`write_file`, `delete_file`, `move_file`, `copy_file`, `create_directory`,
`replace_text`, `file_editor`/`edit_file`, `apply_patch`), every shell command (`execute_command`
and friends - including read-looking ones such as `ls`, because classifying arbitrary command lines
as safe is not a bet worth making), `dataframe_export`, `todo_management`, `http_request` with a
state-changing method, and any call that names an output-file argument
(`output_file`, `output_path`, `save_to`, `save_path`, `destination_path`, `target_path`).

**Anything unclassified is denied.** Plan mode fails closed. MCP tools carry no capability metadata
(see `McpRemoteTool`, which leaves `RequiredPermissions` at `None`), so their effects cannot be
proven and they are refused by default. The same applies to CLI subprocess tools and to any tool a
future package upgrade introduces.

Denied calls return a normal unsuccessful tool result explaining the mode, so the agent reports the
refusal and keeps planning instead of crashing the turn.

## Re-enabling a known read-only tool

If you know a specific MCP or CLI tool is read-only, list it in `.andy/modes.json` - in the project
and/or under your home directory. Both files are read and merged.

```json
{
  "planReadOnlyTools": ["mcp__docs__search", "mcp__jira__get_issue"]
}
```

Entries are exact tool ids; there are no wildcards, so an entry can never widen beyond the one tool
it names, and it can never re-enable a tool the built-in classification already knows is mutating -
listing `write_file` here does nothing. A malformed file contributes no opt-ins (it cannot silently
disable Plan mode) and never breaks start-up.

## How the enforcement is wired

`CliPermissionServiceExtensions.AddAndyCliPermissions` is the one place every entry point (the
interactive TUI, the ACP server, the one-shot command path, and the headless runner) goes through.
It calls `AddAndyPermissions` first and then installs the mode overlay **around** the result:

1. `ModeGatedPermissionAuthorizer` decorates `IToolPermissionAuthorizer`. When the mode forbids a
   call it returns a synthetic `Deny` and never asks the inner authorizer.
2. `ModeGatedToolExecutor` decorates `IToolExecutor`, outside the permission engine's own decorator.
   A forbidden call is short-circuited and the inner executor is never invoked.

Because both sit outside the rule engine, the layered rules cannot reach them. A
`write_file(*)` Allow rule in the user, project, local, session, or injected layer - including the
headless per-run `permissions.allowed_tools` list and a session "Allow" the user granted earlier -
has no effect in Plan mode. Nor does auto-approve (`/auto`), because the approval prompt is only
consulted for `Ask`, and the mode returns `Deny`.

The two decorators are redundant on purpose: the authorizer decoration is what every verdict
consumer sees (the packaged gate, the headless `ObservingToolExecutor`, the end-of-run tool-usage
audit), and the executor decoration guarantees the short-circuit even for a path that skips the
authorizer.

## Mode transitions

`AgentModeState` owns the current mode and enforces one asymmetric rule:

- Entering a restrictive mode is allowed from any source (start-up, user command, session restore,
  headless config).
- **Leaving** a restrictive mode requires `ModeChangeSource.UserCommand` - a human typing
  `/mode build`. A session restore, a start-up flag, or any programmatic path cannot re-enable
  writes for someone who is currently planning.

The model has no way to change modes; there is no mode tool.

## Persistence and resume

The active mode id is written into the session file (`~/.andy/sessions/<id>.json`, `"mode"` field)
on every save. On `--resume` / `--continue` / `/resume`, a saved Plan mode is restored and reported
in the feed. A saved Build mode is applied only if it does not weaken the current mode - resuming a
Build session while in Plan mode leaves you in Plan mode and says so, so you can switch deliberately.

Sessions saved before this feature carry no `mode` field; that is distinguishable from `"build"` and
means "leave the current mode alone".

## Failing closed on an unknown mode

Mode parsing (`AgentModeCatalog.TryParse`) never falls back to a default.

- `andy-cli run --headless ... --mode <unknown>` exits with `HeadlessExitCode.ConfigError` (2)
  before the agent loop starts.
- `andy-cli --mode <unknown>` cannot usefully abort mid-start-up, so it starts in the **most
  restrictive** known mode and shows the error. It never assumes the permissive default.
- `/mode <unknown>` is rejected and leaves the current mode untouched.

## ACP

`AgentModeDefinition.Id` is the stable wire identifier shared by `/mode`, the session file, and the
headless flag. When ACP session-mode switching is implemented it should map its mode ids onto these
and route changes through `AgentModeState.TrySet` with `ModeChangeSource.UserCommand`; the overlay
is already wired for the ACP path because that path shares `AddAndyCliPermissions`. No separate mode
model should be introduced.

## Source map

| File | Role |
| --- | --- |
| `src/Andy.Cli/Modes/AgentMode.cs` | The shared mode abstraction and fail-closed parsing |
| `src/Andy.Cli/Modes/AgentModeState.cs` | Current mode plus the transition rule |
| `src/Andy.Cli/Modes/PlanModeToolPolicy.cs` | Which tools Plan mode allows |
| `src/Andy.Cli/Modes/ModeToolGate.cs` | Mode plus policy to a per-call verdict |
| `src/Andy.Cli/Modes/ModeGatedPermissionAuthorizer.cs` | Overlay over the permission engine |
| `src/Andy.Cli/Modes/ModeGatedToolExecutor.cs` | Overlay over tool execution |
| `src/Andy.Cli/Modes/AgentModePrompt.cs` | Mode text for the model (context, not the boundary) |
| `src/Andy.Cli/Modes/ModeConfigFile.cs` | `.andy/modes.json` read-only opt-ins |
| `src/Andy.Cli/Modes/StartupModeSelector.cs` | `--mode` for the interactive CLI |
| `src/Andy.Cli/Commands/ModeCommand.cs` | The `/mode` command |
| `tests/Andy.Cli.Tests/Modes/` | Tests, including the end-to-end overlay proofs |
