# CLI coding-agent feature comparison for JetBrains Rider

Research date: 2026-07-21

This report compares the CLI coding agents in the question with Andy CLI. It
focuses on commands, product capabilities, and the subset of capabilities that
JetBrains Rider can use through the Agent Client Protocol (ACP).

The comparison is intentionally split into three layers:

1. **Standalone CLI**: everything the agent can do in its own terminal UI or
   headless mode.
2. **ACP server or adapter**: the process Rider starts and talks to over ACP.
3. **Rider experience**: the ACP features implemented by both the agent and the
   JetBrains AI Assistant client. A feature in the standalone CLI is not
   automatically available in Rider.

The [JetBrains activation guide](https://www.jetbrains.com/help/ai-assistant/activate-agents.html)
describes Junie, Claude Agent, Codex, and GitHub Copilot as integrated agents
and allows other agents to be installed from the ACP registry or configured in
`acp.json`. The [ACP Registry](https://github.com/agentclientprotocol/registry)
is updated continuously. Its registry metadata and its generated
[protocol adaptation matrix](https://github.com/agentclientprotocol/registry/blob/main/.protocol-matrix/latest.md)
are the primary sources for Rider launch commands and ACP session capabilities.

## Name corrections and scope

The names below normalize several apparent transcription errors in the request:

- `Agoagentic` is listed by the registry as **Agoragentic**.
- `Github pilot` is treated as **GitHub Copilot CLI**. The registry currently
  contains both a Copilot language-server entry and a Copilot CLI entry with the
  same display name; this report uses the CLI package, `@github/copilot`.
- `Autohand` is now displayed as **Autohand Code**.
- `Factory  Droid` is normalized to **Factory Droid**.

The registry also contains Kimi CLI, but it was not in the requested list and is
therefore not included. Andy CLI is not currently listed in the public ACP
registry; it can be added to Rider as a custom ACP agent.

## Executive comparison

No single agent is best on every dimension. The most useful shortlists are:

| Requirement | Strong candidates | Why |
| --- | --- | --- |
| Deep, mature terminal harness | Claude Agent, Codex, GitHub Copilot, Gemini CLI, Junie | Broad tool loops, safety controls, durable instructions, MCP, and automation surfaces |
| Broad model/provider choice | OpenCode, Kilo, Cline, fast-agent, Goose, Autohand Code, Andy CLI | Provider-neutral or multi-provider configuration instead of one vendor model family |
| Rich Rider session lifecycle | Autohand Code, Claude Agent, GLM Agent, Kilo, Nova, OpenCode | The current ACP probe advertises load, list, fork, and resume |
| Multi-agent orchestration | Factory Droid, DeepAgents, fast-agent, Amp, Gemini CLI, Mistral Vibe | Missions, subagents, pipelines, parallel workflows, or delegated task tools |
| Large-codebase retrieval | Auggie CLI, Codex, Claude Agent, Cursor, Gemini CLI | Strong repository search/context systems; Auggie is especially context-engine focused |
| Data and database work | Cortex Code, Andy CLI | Cortex is Snowflake-native; Andy has 28 DuckDB-backed dataframe tools plus six PDF tools |
| Local-first or local-model use | siGit Code, Andy CLI with Ollama, OpenCode, Kilo, Goose, VT Code | Local inference or configurable local providers; exact tool quality varies by model |
| Infrastructure and DevOps | Stakpak, Cortex Code, Goose, Andy CLI | Stakpak is DevOps-focused; Cortex is Snowflake-focused; Goose and Andy expose extensible command/tool systems |
| Rust-specialized assistance | Corust Agent | Purpose-built Rust positioning rather than a general-purpose agent |
| Strict scripted execution | Codex `exec`, Claude `-p`, Factory `droid exec`, Junie headless, Auggie `--print`, Andy headless | Explicit non-interactive commands and machine-readable output or exit contracts |

This is a feature comparison, not a benchmark. It does not rank model quality,
latency, price, privacy, or patch correctness because those depend on model,
subscription, repository, prompt, and version.

## Rider and ACP command matrix

### Legend

- `Load`: the agent advertises loading a known session.
- `List`: the agent advertises `session/list`.
- `Fork`: the agent advertises `session/fork`.
- `Resume`: the agent advertises `session/resume`.
- `None`: initialization worked, but none of those optional lifecycle methods
  were advertised.
- `N/T`: not included in the latest registry protocol probe. It does not mean
  unsupported.
- `Init error`: the registry probe could not initialize that version. It is a
  point-in-time interoperability warning, not proof that every installation is
  broken.

The launch entries below are the commands or command shapes recorded by the
registry on the research date. Rider normally downloads the package/binary and
constructs the process invocation, so users installing from the registry do not
need to type the versioned command themselves.

| Agent | Registry version | Registry license | ACP launch entry | ACP lifecycle in latest probe |
| --- | ---: | --- | --- | --- |
| Agoragentic | 1.3.0 | MIT | `npx -y agoragentic-mcp@1.3.0 --acp` | N/T |
| Amp | 0.8.1 | Apache-2.0 adapter | `amp-acp` | None |
| Andy CLI | repository build | See repository license | `andy-cli --acp` | Load within current process only |
| Auggie CLI | 0.33.0 | Proprietary | `npx -y @augmentcode/auggie@0.33.0 --acp` | Load, List |
| Autohand Code | 0.2.1 | Apache-2.0 adapter | `npx -y @autohandai/autohand-acp@0.2.1` | Load, List, Fork, Resume |
| Claude Agent | 0.60.0 | Proprietary | `npx -y @agentclientprotocol/claude-agent-acp@0.60.0` | Load, List, Fork, Resume |
| Cline | 3.0.46 | Apache-2.0 | `npx -y cline@3.0.46 --acp` | Load |
| Codebuddy Code | 2.106.7 | Proprietary | `npx -y @tencent-ai/codebuddy-code@2.106.7 --acp` | N/T |
| Codex | 1.1.5 | Apache-2.0 adapter | `npx -y @agentclientprotocol/codex-acp@1.1.5` | Load, List, Resume |
| Cortex Code | 1.0.73 | Proprietary | `cortex acp serve` | Init error in latest probe |
| Corust Agent | 0.6.0 | GPL-3.0-or-later | `corust-agent-acp` | None |
| crow-cli | 0.1.24 | Apache-2.0 | `crow-cli acp` | N/T |
| Cursor | 2026.07.17 | Proprietary | `cursor-agent acp` | Load, List |
| DeepAgents | 0.1.7 | MIT | `npx -y deepagents-acp@0.1.7` | N/T |
| Devin | 3000.2.17 | Proprietary | `devin acp` | Load, List |
| DimCode | 0.2.35 | Proprietary | `npx -y dimcode@0.2.35 acp` | Load, List, Resume |
| Dirac | 0.4.21 | Apache-2.0 | `npx -y dirac-cli@0.4.21 --acp` | Load, Resume |
| Factory Droid | 0.176.0 | Proprietary | `npx -y droid@0.176.0 exec --output-format acp-daemon` | Load, List, Resume |
| fast-agent | 0.9.20 | Apache-2.0 | `uvx fast-agent-acp==0.9.20 -x` | Load, List, Resume |
| Gemini CLI | 0.51.0 | Apache-2.0 | `npx -y @google/gemini-cli@0.51.0 --acp` | Load |
| GitHub Copilot CLI | 1.0.73 | Proprietary | `npx -y @github/copilot@1.0.73 --acp` | Load, List |
| GLM Agent | 1.3.0 | Apache-2.0 | `npx -y glm-acp-agent@1.3.0` | Load, List, Fork, Resume |
| Goose | 1.43.0 | Apache-2.0 | `goose acp` | Load, List |
| Grok Build | 0.2.110 | Proprietary | `npx -y @xai-official/grok@0.2.110 agent stdio` | Load |
| Harn | 0.10.30 | Apache-2.0 | `harn serve acp` | Load, List, Resume |
| Junie | 2383.9.0 | Proprietary | `junie --acp=true` | Registry entry not in the latest public probe |
| Kilo | 7.4.11 | MIT | `kilo acp` | Load, List, Fork, Resume |
| Minion Code | 0.1.44 | AGPL-3.0 | `uvx minion-code@0.1.44 acp` | N/T |
| Mistral Vibe | 2.22.0 | Apache-2.0 | `vibe-acp` | Load, List, Fork |
| Nova | 1.1.29 | Proprietary | `npx -y @compass-ai/nova@1.1.29 acp` | Load, List, Fork, Resume |
| OpenCode | 1.18.4 | MIT | `opencode acp` | Load, List, Fork, Resume |
| pi ACP | 0.0.31 | MIT adapter | `npx -y pi-acp@0.0.31` | Load, List |
| Poolside | 1.0.13 | Proprietary | `pool acp` | Load, List |
| Qoder CLI | 0.2.14 | Proprietary | `npx -y @qoder-ai/qodercli@0.2.14 --acp` | N/T |
| Qwen Code | 0.20.1 | Apache-2.0 | `npx -y @qwen-code/qwen-code@0.20.1 --acp --experimental-skills` | Load, List, Resume |
| siGit Code | 1.4.1 | Apache-2.0 | `sigit` | Load, Fork |
| Stakpak | 0.3.88 | Apache-2.0 | `stakpak acp` | Load |
| VT Code | 0.96.14 | MIT | `vtcode acp` | N/T |

Important interpretation points:

- The license column is the registry distribution or adapter license. It is not
  necessarily the license for the upstream model, hosted service, or underlying
  CLI. For example, an open ACP adapter can still require a proprietary service.
- `Load` does not promise durable cross-process history. Andy CLI, for example,
  can load only sessions still retained in the same server process.
- Model selection, plan modes, slash commands, image input, and custom tools use
  other ACP capability/configuration surfaces and are not fully represented by
  the lifecycle probe.
- The registry metadata may update more quickly than the daily capability probe,
  which is why a few package versions differ from the version shown in the probe.

## Product feature comparison

### First-party and full-stack coding agents

These products provide a broad coding workflow rather than only a thin ACP
bridge. `Evidence: official docs` means the feature summary was checked against
the vendor documentation. `Evidence: registry` means public material was too
thin for a stronger claim.

| Agent | Model and deployment posture | Standalone CLI features and important commands | Extensibility and automation | Evidence |
| --- | --- | --- | --- | --- |
| Claude Agent | Anthropic Claude models; Anthropic account, Console, API key, or JetBrains-provided access depending on surface | `claude` TUI; `claude -p` headless; `-c` continue; `-r`/`--resume`; file editing, shell, git workflows, planning, permissions, and session continuation | MCP via `claude mcp`; `CLAUDE.md`; skills, hooks, custom commands, and subagents | [Official CLI reference](https://docs.anthropic.com/en/docs/claude-code/cli-usage) |
| Codex | OpenAI coding models; ChatGPT or API auth; can also use configured local OSS providers | `codex` TUI; `codex exec`/`e` headless JSONL; `resume`; `fork`; `review`; `sandbox`; live/cached web search; image input; explicit sandbox and approval modes | `AGENTS.md`, skills, plugins, hooks, MCP client/server, app server, SDK, subagents, profiles, and cloud tasks | [Official command reference](https://developers.openai.com/codex/cli/reference/) |
| GitHub Copilot CLI | GitHub Copilot service with GitHub-managed model access | `copilot` terminal agent; coding, shell, planning, code review, research, and GitHub-aware tools; permission and sandbox controls | Custom instructions, skills, tools, MCP, hooks, subagents, custom agents, and plugins; built-in explore/task/review/research agents | [Official overview](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [customization comparison](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/comparing-cli-features) |
| Junie | JetBrains service plus supported model/API-key options in the CLI | `junie` TUI; headless/CI mode; plan and new-session commands; shell passthrough; action allowlist; token/cost usage; `/ide` and `/debug` can use a connected JetBrains IDE | MCP with configuration assistant, agent skills, subagents, custom commands, guidelines/memory, and extension bundles | [Official quickstart](https://junie.jetbrains.com/docs/junie-cli.html), [CLI reference](https://junie.jetbrains.com/docs/parameters.html) |
| Amp | Managed frontier-model modes (`low`, `medium`, `high`, `ultra`) with an alternate-model Oracle | `amp` TUI; `amp -x` execute-and-exit; file/shell tools, image input, shared threads, remote control, and `amp review` | Skills, MCP, plugin API, policy plugins, subagents, checks, SDK, thread sharing, and cloud Orb execution | [Owner's Manual](https://ampcode.com/manual) |
| Auggie CLI | Augment-managed service backed by its codebase Context Engine | `auggie`; `--print`/`-p` with JSON, quiet, or compact output; ask-only mode; queued instructions; images; tool listing and permissions | MCP client config plus `--mcp` server mode; custom commands; reusable Claude command compatibility; codebase retrieval exposed to other agents | [Official CLI reference](https://docs.augmentcode.com/cli/reference) |
| Autohand Code | Multi-provider: OpenRouter, OpenAI, Bedrock, DeepSeek, Azure, Z.ai, local models, and other configured gateways | Context-aware multi-file editing, shell/web context, git integration, planning, memory, provider switching, auto mode, headless and CI operation | `AGENTS.md`, skills, hooks, MCP policy, session history, and agent teams | [Official product docs](https://docs.autohand.ai/working-with-autohand-code/code/) |
| Cline | Multi-provider/BYOK coding agent | `cline` interactive or `cline "task"` headless; file editing, terminal commands, browser use, checkpoints/context, and approval flow | MCP, rules, skills, hooks, CI use, and ACP | [Official CLI reference](https://docs.cline.bot/cli/cli-reference) |
| Codebuddy Code | Tencent Cloud managed coding tool | Registry advertises an official intelligent coding tool; the ACP package is launched with `--acp` | Detailed cross-surface capabilities were not independently verified for this snapshot | [Official site](https://www.codebuddy.cn/cli/), [registry entry](https://github.com/agentclientprotocol/registry/tree/main/codebuddy-code) |
| Cortex Code | Snowflake-native agent with Snowflake authentication and RBAC | `cortex`; `--continue`; `--resume`; `-p`/`-f` batch mode; files, bash, git, SQL, dbt, lineage, worktrees, review, plan/bypass modes, OS/container sandbox | `AGENTS.md`, skills, MCP, custom tools, subagents, hooks, profiles, plugins, and Agent SDK | [Official overview](https://docs.snowflake.com/en/user-guide/cortex-code/cortex-code), [CLI reference](https://docs.snowflake.com/en/user-guide/cortex-code/cli-reference) |
| Cursor | Cursor-managed model catalog and account/API-key access | `cursor-agent`; `--print` with text/JSON/stream-JSON; `--resume`; model selection; file edits, shell, review UI, context mentions, and command approvals | MCP; `.cursor/rules`; `AGENTS.md` and `CLAUDE.md`; session history | [Official parameter reference](https://docs.cursor.com/en/cli/reference/parameters) |
| Devin | Cognition-managed Devin service and environments | Terminal coding session with persisted sessions, planning, shell output, browser capabilities, and resume support; the ACP surface can attach to richer Devin resources where enabled | Hosted integrations and MCP; public release notes describe expanding ACP methods for repositories, secrets, deploys, session archive, and browser attachment | [Official CLI docs](https://docs.devin.ai/cli), [release notes](https://docs.devin.ai/release-notes/overview) |
| Factory Droid | Factory service with multiple selectable models and BYOK options | `droid`; `droid exec` structured headless mode; Auto vs Spec planning; graded autonomy; file/shell/web tools; review; browser/desktop/terminal control | Custom Droids/subagents, Missions multi-agent orchestration, skills, MCP, plugins, commands, and SDK | [Headless reference](https://docs.factory.ai/cli/droid-exec/overview), [Custom Droids](https://docs.factory.ai/cli/configuration/custom-droids) |
| Gemini CLI | Google Gemini via Google login/API/Vertex options | `gemini`; headless prompts; file/search/shell/web tools, planning, policies, automatic file checkpoints, session checkpoints, and a browser subagent | `GEMINI.md`, skills, hooks, MCP, extensions, local and remote A2A subagents, custom commands | [Official command reference](https://geminicli.com/docs/reference/commands/), [subagents](https://geminicli.com/docs/core/subagents/) |
| Grok Build | xAI-managed coding agent and model access | `grok`; headless/scripted execution; `grok inspect`; ACP through `grok agent stdio`; local coding tool loop | Rules, skills, plugins, hooks, and MCP are discoverable by `grok inspect` | [Official overview](https://docs.x.ai/build/overview), [CLI reference](https://docs.x.ai/build/cli/reference) |
| Poolside | Poolside standalone service or enterprise deployment and Poolside coding models | `pool`; `pool exec`; persisted history; file editing and commands; approval modes, plan mode, local/managed sandboxes, structured automation | `AGENTS.md`, skills, MCP, configurable models/agents, trajectory history, and ACP setup for JetBrains | [Official CLI overview](https://docs.poolside.ai/cli/pool), [ACP integration](https://docs.poolside.ai/cli/editor-integration) |
| Qoder CLI | Proprietary Qoder service | Registry advertises an agentic coding assistant and an npm ACP entry | Public command/extensibility details were not sufficiently documented in the sources checked; verify in `qoder --help` before automation | [Official site](https://qoder.com), [registry entry](https://github.com/agentclientprotocol/registry/tree/main/qoder) |

### Open-source and model-flexible agents

| Agent | Model and deployment posture | Standalone features and differentiator | Extensibility and automation | Evidence |
| --- | --- | --- | --- | --- |
| DeepAgents | Provider-agnostic LangChain/LangGraph harness; local or remote sandbox backends | `dcode`; files, shell, web search/fetch, planning, persistent memory, automatic compaction/offloading, human approval, and remote sandboxes | Subagents, skills, MCP, hooks, tracing, pluggable backends, SDK, and ACP package | [Official Deep Agents Code docs](https://docs.langchain.com/oss/javascript/deepagents/code/overview) |
| Dirac | Open-source agent optimized to reduce token/API cost | Hash-anchored parallel edits and AST manipulation are the stated differentiators; general coding-agent workflow | ACP adapter is native to its CLI package; broader extension claims were not used without documentation | [Official repository](https://github.com/dirac-run/dirac) |
| fast-agent | Broad provider support including Anthropic, OpenAI, Google, Azure, Ollama, DeepSeek, and generic/local models | CLI-first coding agent and agent-development framework; multimodal input, shell mode, structured output, evaluation and replay | Strong MCP coverage; skills; Python function tools; chain, parallel, router, orchestrator, evaluator-optimizer, MAKER, and agents-as-tools workflows | [Official repository](https://github.com/evalstate/fast-agent) |
| Goose | Open-source, local, provider-configurable agent | Engineering automation through file and shell tools with reusable sessions; designed to work locally | Extensions and recipes are central concepts; MCP-based tool ecosystem and ACP mode | [Official documentation](https://block.github.io/goose/) |
| Kilo | Open-source, multi-provider agent | `kilo`; `kilo run`; `serve`; web UI; model/auth management; session export/import; timeline, fork, undo/redo, compact, review, and AGENTS initialization | Local/remote MCP, agents, custom modes, rules, skills, server mode, and ACP | [Official CLI reference](https://kilo.ai/docs/code-with-ai/platforms/cli) |
| Minion Code | AGPL Minion-based coding assistant | Registry describes rich development tools built on the Minion framework | ACP entry uses `uvx`; other feature claims were not independently verified | [Official repository](https://github.com/femto/minion-code) |
| Mistral Vibe | Mistral-first open-source agent with configurable models/providers | `vibe`; prompt/programmatic mode; read/write/patch, stateful bash, grep, todo, web search/fetch, project context, voice preview, and approval profiles | Skills standard, MCP, custom tools/commands/agents, subagents, hooks, session persistence, and ACP | [Official repository](https://github.com/mistralai/mistral-vibe) |
| OpenCode | Open-source, broad provider/model support | Terminal, desktop, and server surfaces; files, edits, grep/glob/list, shell, LSP, web fetch/search, todo, session management, and detailed permission rules | Primary agents and subagents, skills, custom tools, MCP, commands, and ACP | [Official agents docs](https://opencode.ai/docs/agents/), [tools docs](https://opencode.ai/docs/tools/) |
| pi ACP | MIT ACP adapter around the Pi coding agent | Deliberately small, extensible coding-agent core; adapter exposes Pi in ACP clients | Pi's ecosystem supplies extensions, skills, themes, model/provider configuration, and custom tools; exact behavior depends on installed extensions | [Adapter repository](https://github.com/svkozak/pi-acp) |
| Qwen Code | Apache-2.0, Qwen-first with configurable endpoints/providers | Files, shell and background process monitoring, web fetch, todo, plan mode, sandboxing, and delegated agent tool | `QWEN.md`, skills, MCP, subagents, extensions, custom commands, and SDKs | [Official tool overview](https://qwenlm.github.io/qwen-code-docs/en/developers/tools/introduction/), [skills](https://qwenlm.github.io/qwen-code-docs/en/users/features/skills/) |
| siGit Code | Local-first; optional on-device Onde inference, with Qwen 3 used for local tool calling | Local TUI/ACP agent with file read/write, command, and website tools; no cloud round-trip is required for local mode | ACP-native and can share its local model cache with the desktop app; narrower extension surface than the large harnesses | [Official repository](https://github.com/getsigit/sigit), [package docs](https://docs.rs/crate/sigit/latest) |
| VT Code | Open-source multi-provider agent with automatic provider failover | Registry emphasizes LLM-native code understanding, robust shell safety, and efficient context management | ACP uses the same tool policies as the CLI; verify detailed provider/tool configuration in the project guide | [Official repository](https://github.com/vinhnx/VTCode), [ACP guide](https://github.com/vinhnx/VTCode/blob/main/docs/guides/zed-acp.md) |
| crow-cli | Minimal Apache-2.0 ACP-native coding agent | Deliberately small rather than a feature-heavy harness | Native `crow-cli acp`; other product features were not independently verified | [Official repository](https://github.com/crow-cli/crow-cli) |
| DimCode | Proprietary agent described as putting leading models at the user's command | ACP session load/list/resume worked in the current probe | Product-level tools and extension claims were not independently verified | [Official ACP docs](https://dimcode.dev/docs/acp.html) |
| Nova | Proprietary Compass AI software-engineer agent with a public repository | Registry positions it as a full software engineer; ACP has one of the richer lifecycle sets | Detailed standalone tool and extension claims were not used without stronger docs | [Official repository](https://github.com/Compass-Agentic-Platform/nova) |

### Specialized and workflow-oriented agents

| Agent | Specialization | What is different | Evidence |
| --- | --- | --- | --- |
| Agoragentic | Agent marketplace | Exposes 174+ paid AI capabilities, with USDC settlement on Base L2. This is closer to a capability marketplace than a conventional local coding harness. | [Official repository](https://github.com/rhein1/agoragentic-integrations) |
| Corust Agent | Rust development | Positions itself as a seasoned Rust co-builder and ships a native ACP binary. The latest ACP probe initialized but advertised none of the optional lifecycle methods in this report. | [Official site](https://corust.ai/), [registry entry](https://github.com/agentclientprotocol/registry/tree/main/corust-agent) |
| GLM Agent | Z.ai GLM Coding Plan | GLM 5.x/4.x models, streaming tools, mid-session model switching, image input through a Vision MCP, and on-disk load/fork/resume. | [Registry/Zed documentation](https://zed.dev/acp/agent/glm-acp-agent) |
| Harn | Declarative agent pipelines | Runs `.harn` pipelines as a native ACP coding agent over stdio rather than exposing only one fixed harness. | [Official repository](https://github.com/burin-labs/harn) |
| Stakpak | DevOps and infrastructure | Rust-based open-source DevOps agent with an enterprise-security focus, rather than a general application-code assistant. | [Official repository](https://github.com/stakpak/agent) |

## Andy CLI in detail

Andy CLI is materially different from most entries because its Engine and Tools
are reusable .NET packages and because the repository exposes three distinct
execution modes. The current code also carries an explicit alpha warning.

### Architecture

| Layer | Current implementation |
| --- | --- |
| Agent engine | `SimpleAgent` from **Andy.Engine** supplies the iterative agent/tool loop. `SimpleAssistantService` wraps it for the interactive application. |
| Models | **Andy.Llm** plus a centralized provider registry. Supported providers are OpenRouter, OpenAI, Anthropic, Cerebras, Groq, Google Gemini, and local Ollama. |
| Tools | **Andy.Tools**, **Andy.Tools.Data**, and **Andy.Tools.Pdf** provide the registry, execution framework, DuckDB dataframe tools, and managed PDF tools. |
| Permissions | **Andy.Permissions** gates tool execution. Interactive mode can ask; non-interactive modes fail closed unless explicitly configured. |
| External tools | **Andy.MCP** supports remote MCP tools. Headless configuration also supports subprocess CLI tools. |
| ACP | **Andy.Acp.Core** hosts the ACP server used by Rider/Zed and `AndyAgentProvider` maps ACP sessions to per-session Engine agents. |
| UI | **Andy.Tui** provides the streaming terminal interface, command palette, themes, tool traces, diffs, and performance HUD. |

### Commands and modes

Assuming a published executable named `andy-cli`:

| Purpose | Command |
| --- | --- |
| Interactive TUI | `andy-cli` |
| Rider/ACP server | `andy-cli --acp` |
| Strict headless run | `andy-cli run --headless --config /path/to/run.json` |
| Version | `andy-cli --version` |
| List models | `andy-cli model list` or `/model list` |
| Switch model | `/model switch <model>` |
| Switch provider | `andy-cli model provider <name>` or `/model provider <name>` |
| Provider diagnostics | `andy-cli model detect` |
| List tools | `andy-cli tools list` or `/tools list` |
| Tool detail | `andy-cli tools info <tool_id>` or `/tools info <tool_id>` |
| Permission manager | `/permissions` |
| Theme manager | `/theme` |

Headless mode uses a closed, versioned JSON schema and provides structured exit
codes for success, agent failure, configuration error, cancellation, timeout,
and internal error. It supports:

- Explicit agent instructions and optional JSON output enforcement.
- Explicit provider/model selection and environment-variable key references.
- Workspace and expected-branch validation.
- Maximum iteration and wall-clock limits.
- NDJSON progress events to stdout or a FIFO.
- Built-in tools plus configured `cli` subprocess tools and remote `mcp` tools.
- A per-run `permissions.allowed_tools` allowlist for mutating operations.
- Bounded `required_actions` assertions that verify actual successful tool
  outcomes and prevent false-success output publication.
- Optional atomic per-run transcripts with secret redaction, record/run bounds,
  deterministic retention, and terminal outcome evidence.

### Built-in Andy Tools catalog

The command `dotnet run --project src/Andy.Cli -- tools list` was executed for
this report and returned **54 tools**.

| Group | Count | Tool IDs |
| --- | ---: | --- |
| File and code | 8 | `code_index`, `copy_file`, `create_directory`, `delete_file`, `list_directory`, `move_file`, `read_file`, `write_file` |
| Text processing | 3 | `format_text`, `replace_text`, `search_text` |
| System | 3 | `execute_command`, `process_info`, `system_info` |
| Git | 1 | `git_diff` |
| Web/data interchange | 2 | `http_request`, `json_processor` |
| Utility/productivity | 3 | `datetime_tool`, `encoding_tool`, `todo_management` |
| PDF | 6 | `pdf_extract_tables`, `pdf_extract_text`, `pdf_info`, `pdf_outline`, `pdf_reflow`, `pdf_search` |
| Dataframe | 28 | See the detailed list below |

The 28 DuckDB-backed dataframe tools are:

`dataframe_assert`, `dataframe_distinct`, `dataframe_drop`,
`dataframe_dropna`, `dataframe_export`, `dataframe_fillna`,
`dataframe_filter`, `dataframe_group_by`, `dataframe_join`, `dataframe_list`,
`dataframe_load_csv`, `dataframe_load_delta`, `dataframe_load_json`,
`dataframe_load_parquet`, `dataframe_pivot`, `dataframe_preview`,
`dataframe_profile`, `dataframe_rename`, `dataframe_sample`,
`dataframe_schema`, `dataframe_select`, `dataframe_sort`, `dataframe_union`,
`dataframe_unnest`, `dataframe_unpivot`, `dataframe_value_counts`,
`dataframe_window`, and `dataframe_with_column`.

These are structured operations, not arbitrary SQL execution. They cover CSV,
JSON, Parquet, and Delta loading; schema/profile/preview/assertion; filtering,
expressions, grouping, windows, pivots, joins, null handling, sampling, sorting,
union, export, and dataset lifecycle.

### Andy ACP behavior in Rider

Andy advertises and implements:

- New sessions with one Engine agent per ACP session.
- Loading a session while it remains in the current server's bounded in-memory
  session registry.
- Embedded text/resource context from the client.
- Cancellation propagated to the active Engine operation.
- Provider/model identification on the first prompt.
- A grouped per-session provider/model picker through ACP config options.
- Progress narration, tool-start updates, real tool results, and final output.

Current ACP limitations are important:

- Session history is not persisted across process restarts.
- Loading a retained session returns metadata but cannot replay the transcript.
- Session list, fork, durable resume, and mode switching are not implemented.
- Image and audio prompt capabilities are not advertised.
- Final model text is sent as one completed chunk because the Engine does not yet
  expose token-level response streaming.
- Andy is not in the public ACP registry, so Rider requires custom-agent setup.

A manual Rider configuration has this shape after publishing Andy CLI:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "/absolute/path/to/andy-cli",
      "args": ["--acp"],
      "env": {
        "OPENAI_API_KEY": "use-a-securely-injected-value"
      }
    }
  }
}
```

Prefer injecting secrets through the IDE/OS environment or a supported secure
credential mechanism rather than committing them to `acp.json`.

### Andy's strongest differentiators and largest gaps

| Dimension | Andy CLI today | Comparison impact |
| --- | --- | --- |
| Provider choice | Seven remote/local provider paths with automatic detection and per-session ACP selection | Stronger choice than single-vendor agents; the picker currently exposes the configured/default model for each available provider rather than a live model catalog |
| Built-in structured data | 28 dataframe and 6 PDF tools | Unusually strong for repository-plus-data workflows |
| .NET architecture | Engine, LLM, Tools, Permissions, MCP, ACP, and TUI are separate packages | Good for reuse and embedding in .NET systems |
| Headless contract | Versioned schema, limits, permission allowlist, event stream, atomic output, required-action verification, and bounded redacted transcripts | Strong automation foundation and clearer failure/diagnostic semantics than an ad-hoc prompt flag |
| Tool safety | Interactive permission manager; headless mutating tools fail closed | Good architecture, but the repository explicitly warns that permissions are alpha and not fully tested |
| Repository work | File/text/git/shell/code-index tools | Covers the core loop, though no dedicated LSP/refactoring engine is exposed as a tool |
| Web capability | Raw `http_request`; no first-class web search or browser automation | Behind Codex, Gemini, Cline, Factory, DeepAgents, and similar agents for web/UI validation |
| Multimodality | PDF parsing is strong; ACP image/audio prompts are absent | Document analysis is strong, visual debugging is weak |
| Customization | Provider/model settings, system prompt, built-in registry, MCP and CLI tool bindings | No user-facing skills, plugins, hooks, custom subagents, or portable agent profiles yet |
| Multi-agent work | Single Engine agent per session | Behind Factory Missions, fast-agent workflows, DeepAgents, Gemini subagents, Amp, and Mistral Vibe |
| ACP lifecycle | Create, in-process load, embedded context, cancel, progress | Behind agents that advertise list/fork/resume and persistent histories |
| Distribution | Source/build/publish workflow; no ACP registry entry | More setup in Rider than one-click registry agents |

### Improvement backlog derived from this comparison

The 2026-07-21 backlog grooming converted the largest actionable gaps into
scoped GitHub work:

| Gap | Tracking issue |
| --- | --- |
| Incremental ACP final responses and rich editor-native tool content | [#204](https://github.com/rivoli-ai/andy-cli/issues/204) |
| ACP provider/model selection (delivered) | [#205](https://github.com/rivoli-ai/andy-cli/issues/205) |
| Persistent sessions, replay, catalog, and resume | [#206](https://github.com/rivoli-ai/andy-cli/issues/206) |
| Bounded subagent delegation and parallel work | [#210](https://github.com/rivoli-ai/andy-cli/issues/210) |
| Repository instructions and reusable skills | [#212](https://github.com/rivoli-ai/andy-cli/issues/212) |
| First-class web search and browser validation | [#213](https://github.com/rivoli-ai/andy-cli/issues/213) |
| ACP registry packaging and editor installation | [#214](https://github.com/rivoli-ai/andy-cli/issues/214) |
| Multimodal prompts across interactive, headless, and ACP | [#215](https://github.com/rivoli-ai/andy-cli/issues/215) |

These are grouped under
[#217](https://github.com/rivoli-ai/andy-cli/issues/217), which focuses on
high-value workflow outcomes rather than feature-count parity.

## Practical selection guidance

### Choose Andy CLI when

- A .NET-native, reusable agent stack matters.
- The same agent must run in a TUI, strict headless job, and ACP editor mode.
- Structured dataframe or PDF analysis should be available directly to the
  coding agent.
- Explicit provider selection, local Ollama, structured exit codes, and
  fail-closed headless mutations are more important than a large plugin market.
- The alpha-stage security and feature limitations are acceptable in a
  non-production, backed-up environment.

### Choose a mature vendor CLI when

- You need polished sessions, image/browser workflows, hosted integrations,
  enterprise administration, or a turnkey model/service contract.
- You rely on a large ecosystem of skills, plugins, hooks, custom agents, and
  packaged MCP integrations.
- Persistent/forkable Rider sessions are a hard requirement today.

### Choose an open, model-flexible agent when

- BYOK, local models, or provider portability is the primary concern.
- You want to inspect or extend the agent harness rather than use a managed
  vendor service.
- You are willing to validate each ACP adapter's maturity and permission model.

## Sources and verification notes

Primary cross-agent sources:

- [JetBrains: Activate agents](https://www.jetbrains.com/help/ai-assistant/activate-agents.html)
- [JetBrains: ACP registry announcement](https://blog.jetbrains.com/ai/2026/01/acp-agent-registry/)
- [ACP Registry](https://github.com/agentclientprotocol/registry)
- [ACP protocol adaptation matrix](https://github.com/agentclientprotocol/registry/blob/main/.protocol-matrix/latest.md)

Andy CLI sources:

- [`README.md`](../README.md)
- [`ToolCatalog.cs`](../src/Andy.Cli/Services/ToolCatalog.cs)
- [`AndyAgentProvider.cs`](../src/Andy.Cli/ACP/AndyAgentProvider.cs)
- [`headless-runtime.md`](headless-runtime.md)
- [`tool-execution-architecture.md`](tool-execution-architecture.md)
- [`ZED_INTEGRATION.md`](ZED_INTEGRATION.md)

Verification caveats:

- Commands and registry versions are time-sensitive. Recheck the registry and
  each agent's `--help` output before pinning automation.
- Vendor names, subscriptions, model availability, and authentication methods
  can change independently of the ACP adapter.
- The protocol matrix is a synthetic unauthenticated/auth-bound probe. It is
  excellent evidence for method availability but does not test coding quality,
  every authenticated workflow, or Rider UI exposure.
- `N/T` rows should be validated manually in Rider; they are not negative
  capability claims.
