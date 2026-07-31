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
/mode                      show the current mode and the available modes
/mode plan                 switch to read-only planning
/mode build                switch back to full capability

/mode grants               review the Plan-mode read-only tool opt-ins
/mode allow <tool-id>      opt specific tools into Plan mode
/mode allow-server <name>  opt in every tool from an MCP server
/mode revoke <id|name>     remove an opt-in
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
future package upgrade introduces. MCP tools can be opted back in - see
[Opting MCP tools into Plan mode](#opting-mcp-tools-into-plan-mode).

Denied calls return a normal unsuccessful tool result explaining the mode, so the agent reports the
refusal and keeps planning instead of crashing the turn.

## Opting MCP tools into Plan mode

The fail-closed default means a freshly connected MCP server is unusable in Plan mode until you say
otherwise. Rather than leaving you to discover that when a plan turn fails, Andy offers the choice
**when the server connects**.

### The connection-time offer

When an interactive session connects an MCP server that exposes tools Plan mode would deny, an
overlay lists those tools and offers:

| Key | Effect |
| --- | --- |
| `A` | Allow **all** tools from this server, including ones it exposes later |
| `Space` | Tick/untick the highlighted tool |
| `Enter` | Allow **only** the ticked tools |
| `N` / `Esc` | Skip - nothing is granted, the tools stay denied |
| Up/Down | Move through the list |

Nothing is granted unless you pick `A` or `Enter`. Skipping records only that the offer was shown.

The offer is raised once per server. It is **not** raised again on later starts - unless that server
exposes a tool it has never offered you, in which case the new tool is surfaced on its own. That
keeps a declined server from nagging while making sure a newly added tool cannot slip past unseen.

The offer only ever appears in the interactive TUI. Headless, ACP, and one-shot runs never prompt:
they read the persisted grants and otherwise stay denied.

### Granting without the TUI

The same decisions are available as commands, for scripting or for when you already know what you
want. They work both as slash commands in the TUI and as `andy-cli mode ...` from a shell, and they
write to your user file:

```
andy-cli mode grants                        # review what is currently opted in
andy-cli mode allow mcp_docs_search         # opt in specific tool ids
andy-cli mode allow-server docs             # opt in a whole MCP server
andy-cli mode revoke mcp_docs_search        # remove a per-tool opt-in
andy-cli mode revoke docs                   # remove a server-wide opt-in
```

### The two grant shapes

- **Per tool** (`planReadOnlyTools`): exact tool ids, no wildcards. Covers only the ids listed. A
  tool the server adds tomorrow stays denied until you opt it in.
- **Per server** (`planReadOnlyMcpServers`): matches every tool id starting with
  `mcp_<server>_`, which is the id shape the MCP host generates. This is the only grant that covers
  tools discovered later - that is exactly what "allow all from this server" means, and it is why
  the two are recorded separately.

### Grants are per developer

Grants live **only** in your user file, `~/.andy/modes.json`. The offer and every `/mode` grant verb
write there, and nothing else is read.

A project `.andy/modes.json` **cannot** supply grants. This is deliberate: that file is committed, so
honoring it would hand Plan-mode access to every teammate who clones the repository without any of
them ever seeing the opt-in prompt. Deciding that an MCP tool is safe to run while planning is a
judgement each developer makes for themselves.

```json
{
  "planReadOnlyTools": ["mcp_jira_get_issue"],
  "planReadOnlyMcpServers": ["docs"],
  "mcpPlanOptInAsked": { "docs": ["mcp_docs_search", "mcp_docs_fetch"] }
}
```

`mcpPlanOptInAsked` is the bookkeeping for the "do not nag" rule above. It grants nothing, and it is
per developer too - so a teammate who has never been offered a server still gets the prompt.

Never commit these keys. If a project `.andy/modes.json` does contain `planReadOnlyTools`,
`planReadOnlyMcpServers`, or `mcpPlanOptInAsked`, they are **ignored** and Andy prints a diagnostic
at start-up (and in `/mode grants`) naming the entries and pointing at your user file. Ignoring fails
in the safe direction: the tools stay denied until you opt in yourself.

A malformed file contributes no opt-ins - it cannot silently disable Plan mode - and never breaks
start-up.

### What a grant can never do

A grant is a **read-only opt-in**, and opt-ins are consulted last, after every capability-based
denial:

- `/mode allow write_file` is refused outright, and nothing is written.
- Hand-editing `write_file` or `execute_command` into `planReadOnlyTools` has no effect either;
  the policy checks the mutating classification before it looks at any opt-in.
- A server-wide grant cannot smuggle in a mutating built-in, and it does not disable the
  parameter-level checks: a granted MCP tool called with an `output_file` argument is still denied,
  because that call writes.

In short, a grant can only rescue calls that would otherwise be denied for being *unclassified*. It
can never widen Plan mode past the mutation boundary.

### Reviewing and revoking

`andy-cli mode grants` (or `/mode grants`) prints every opt-in in force, the user file they are
stored in, and how to remove one. It also lists any project-scope entries that are being ignored, so
a committed file that is quietly doing nothing is visible rather than mysterious. `revoke` takes
either a tool id or a server name - whichever you see in the listing - and edits your user file.

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
| `src/Andy.Cli/Modes/ModeConfigFile.cs` | `.andy/modes.json` on-disk shape |
| `src/Andy.Cli/Modes/PlanModeGrantStore.cs` | Reading, granting and revoking the opt-ins |
| `src/Andy.Cli/Modes/McpToolNaming.cs` | The MCP tool-id convention server-wide grants match on |
| `src/Andy.Cli/Widgets/McpPlanOptInPrompt.cs` | The connection-time opt-in offer |
| `src/Andy.Cli/Modes/StartupModeSelector.cs` | `--mode` for the interactive CLI |
| `src/Andy.Cli/Commands/ModeCommand.cs` | The `/mode` command |
| `tests/Andy.Cli.Tests/Modes/` | Tests, including the end-to-end overlay proofs |
