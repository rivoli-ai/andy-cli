# Andy CLI documentation

Updated: 2026-07-23

Use this index to find the maintained operational and architecture documents.
Historical decisions remain in `adr/`; current behavior belongs in the guides
linked below.

## Using Andy CLI

- [Commands and keyboard shortcuts](README_COMMANDS.md) - Interactive, command-line,
  headless, and ACP invocations.
- [Zed quickstart](QUICKSTART_ZED.md) - Minimal ACP setup for a local build.
- [Zed and ACP integration](ZED_INTEGRATION.md) - Publishing, configuration,
  supported behavior, limitations, and troubleshooting.
- [CLI coding-agent comparison](CLI_AGENT_FEATURE_COMPARISON.md) - Current Rider/ACP
  command and feature comparison, including Andy Engine and Tools.
- [Interactive MCP configuration](mcp-configuration.md) - Project/appsettings
  configuration, stdio and HTTP transports, commands, security, and troubleshooting.

## Headless runtime

- [Headless runtime](headless-runtime.md) - Versioned config, provider/tool wiring,
  permissions, durable redacted transcripts, limits, output, and exit codes.
- [Event stream](event-stream.md) - NDJSON event contract.
- [ADR 0001](adr/0001-headless-agent-runtime.md) - Why one binary hosts interactive
  and headless execution.
- [ADR 0002](adr/0002-headless-v1-inactive-fields.md) - Why inactive v1 fields were
  implemented or rejected.

## Development and operations

- [Continuous integration](CI.md) - Validation and release workflows plus local
  equivalents.
- [SDK and dependencies](SDK_AND_DEPENDENCIES.md) - .NET 8 policy, locked restore,
  dependency manifest, and source compatibility status.
- [Tool execution architecture](tool-execution-architecture.md) - Engine/tool adapter,
  permission, progress, and result flow.
- [Refactoring plan](REFACTORING_PLAN.md) - Current maintainability work and task
  tracking.
- [FeedView inventory](feedview-inventory.md) - Feed item ownership and extraction
  recommendations.

## Source-of-truth policy

- JSON wire contracts are authoritative in `schemas/`.
- Dependency versions are authoritative in `dependency-manifest.json`, project
  files, and committed NuGet lock files.
- Provider defaults are authoritative in `src/Andy.Cli/appsettings.json`; provider
  aliases and detection priority are authoritative in
  `src/Andy.Cli/Services/ProviderRegistry.cs`.
- The live built-in tool catalog is assembled by
  `src/Andy.Cli/Services/ToolCatalog.cs` and can be inspected with
  `andy-cli tools list`.
- Accepted ADRs record decisions at the time they were made. If an ADR and a live
  runtime guide appear to differ, verify the current schema and implementation
  before changing either.
