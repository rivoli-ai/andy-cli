Now I have a comprehensive understanding. Let me compile the research report.

---

# Research Report: HeadlessToolHost / CliSubprocessTool Code Paths, `$ANDY_MCP_URL` Gap, and Downstream Consumers

## 1. Question

Where are the code paths in `HeadlessToolHost` and `CliSubprocessTool`? Where is `$ANDY_MCP_URL` documented but not consumed in code? What does the AQ specification text say about this contract? Who depends on these contracts downstream?

## 2. Findings

### HeadlessToolHost Code Path

- **Class definition**: `src/Andy.Cli/Headless/HeadlessToolHost.cs:24` — `public sealed class HeadlessToolHost : IAsyncDisposable`
- **Constructor**: `:29` — takes `IToolRegistry` + optional `ILoggerFactory`
- **Entry point**: `:37` — `static async Task<HeadlessToolHost> BuildAsync(IReadOnlyList<HeadlessTool> tools, IToolRegistry registry, ILoggerFactory?, CancellationToken)`
- **CLI transport dispatch**: `:56-60` — `case "cli"` creates a `CliSubprocessTool(tool, logger)` and calls `Register(registry, adapter)`
- **MCP transport dispatch**: `:62-98` — `case "mcp"` connects one `McpClient` per distinct endpoint URL (connection reuse), lists remote tools, matches by name, creates `McpRemoteTool`, calls `Register`. If the endpoint doesn't advertise the named tool, throws with a hard error at `:87-89` (comment: "The configurator (Epic AP3) and the agent registry (Epic W) are supposed to agree on tool names")
- **MCP connection**: `:110-133` — `ConnectMcpAsync` uses `StreamableHttpClientTransport` with `Endpoint = new Uri(endpoint)`. At `:126`, reads `ANDY_TOKEN` from env and attaches as `Authorization: Bearer` header. Does **not** read `ANDY_MCP_URL`.
- **Registration**: `:136-139` — `Register(IToolRegistry, ITool)` calls `registry.RegisterTool(tool.Metadata, _ => tool, configuration: new())`
- **Disposal**: `:141-155` — disposes all `McpClient` instances

### CliSubprocessTool Code Path

- **Class definition**: `src/Andy.Cli/Headless/Tools/CliSubprocessTool.cs:25` — `public sealed class CliSubprocessTool : ITool`
- **Constructor**: `:30` — takes `HeadlessTool config` + optional `ILogger<CliSubprocessTool>`, builds `ToolMetadata`
- **Parameter validation**: `:43-68` — `ValidateParameters` checks `args` is an `IEnumerable` of strings with no NUL bytes
- **Execution**: `:76-128` — `ExecuteAsync` calls `MaterializeArgv`, spawns a `Process` with `ProcessStartInfo` using `ArgumentList` (no shell), waits for exit, returns `ToolResult.Failure` for non-zero exit or `ToolResult.Success(stdoutText)` for zero exit
- **Argv materialization**: `:131-152` — `MaterializeArgv` prepends `_config.Command` (if set) or `_config.Binary`, then appends LLM-supplied `args` array
- **Metadata**: `:158-186` — single parameter `args` (array, optional): "Arguments to append to the configured command prefix"

### Caller: HeadlessAgentRunner

- `src/Andy.Cli/Headless/HeadlessAgentRunner.cs:81-82` — `await using HeadlessToolHost? toolHost = await TryBuildToolHostAsync(config, services, loggerFactory, emitter, stderr, ct)`
- `:330-341` — `TryBuildToolHostAsync` wraps `HeadlessToolHost.BuildAsync(config.Tools, registry, loggerFactory, ct)` in try/catch, emitting error events on failure

### `$ANDY_MCP_URL`: Documented but Never Read in Code

`$ANDY_MCP_URL` appears in **five** locations across docs/schema — **zero** in C# source:

| Location | Line | Context |
|---|---|---|
| `schemas/headless-config.v1.json` | :95 | `"description": "mcp-gateway URL for this tool (usually $ANDY_MCP_URL/<tool-name>)."` |
| `schemas/headless-config.v1.json` | :142 | `"description": "...Reserved: ANDY_PROXY_URL, ANDY_TOKEN, ANDY_MCP_URL are always set by the container runtime (Epic Y5)."` |
| `docs/headless-runtime.md` | :250 | Endpoint field: "usually `$ANDY_MCP_URL/<tool-name>`" |
| `docs/headless-runtime.md` | :265 | Env vars table: "`ANDY_MCP_URL` — Base URL of the mcp-gateway. MCP tool endpoints are typically `$ANDY_MCP_URL/<tool-name>`." |

**Critical gap**: The C# code never calls `Environment.GetEnvironmentVariable("ANDY_MCP_URL")`. The search for `ANDY_MCP_URL` in `*.cs` files returned **zero results**. By contrast, `ANDY_TOKEN` is read at `HeadlessToolHost.cs:126`. This means `ANDY_MCP_URL` is purely a **configurator-side convenience variable** — the `andy-containers` configurator reads it at config-generation time and substitutes it into each MCP tool's `endpoint` field before writing the JSON config file. The CLI runtime never sees or expands the raw env var; it receives already-resolved absolute URIs in `tool.endpoint`.

### AQ Specification References

**Note**: "AQ6" does not exist in this codebase. The AQ epic references found are:

- **Epic AQ** (parent): `docs/headless-runtime.md:14` — "Epic AQ (rivoli-ai/andy-cli#44)"
- **AQ1** (schema): `src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs:4` — "C# model of schemas/headless-config.v1.json (AQ1, rivoli-ai/andy-cli#46)"
- **AQ2** (exit codes): `src/Andy.Cli/HeadlessConfig/HeadlessExitCode.cs:3` — "Structured process exit codes per the AQ2 contract (rivoli-ai/andy-cli#47)"
- **AQ3** (agent loop): `src/Andy.Cli/Headless/HeadlessAgentRunner.cs:18` — "AQ3 (rivoli-ai/andy-cli#44) agent execution loop"
- **AQ5** (cancel protocol): `src/Andy.Cli/Program.cs:89` — "AQ5 (rivoli-ai/andy-cli#50): cancel protocol for headless runs"

The headless-runtime doc contract for `$ANDY_MCP_URL` (`docs/headless-runtime.md:265`):
> `ANDY_MCP_URL` | Base URL of the mcp-gateway. MCP tool endpoints are typically `$ANDY_MCP_URL/<tool-name>`.

And the schema `env_vars` field (`schemas/headless-config.v1.json:142`):
> "Reserved: ANDY_PROXY_URL, ANDY_TOKEN, ANDY_MCP_URL are always set by the container runtime (Epic Y5)."

### Downstream Consumers (andy-containers Configurator)

1. **andy-containers configurator (Epic AP3/AP4)**: Generates the headless config JSON. Consumed via `HeadlessConfigContract.ValidateConfig()` at `src/Andy.Cli.Headless.Contract/HeadlessConfigContract.cs:13` — "Generators such as andy-containers and conductor can call `ValidateConfig(string)` to confirm a produced configuration matches the contract before launching `andy-cli run --headless`."

2. **andy-containers exit code handler (Epic AP)**: Keys off `HeadlessExitCode` enum values at `src/Andy.Cli/HeadlessConfig/HeadlessExitCode.cs:4-6` — "Consumers in andy-containers (Epic AP configurator + captor) key off these values to decide retry/cancel/report semantics — changing a mapping is a breaking cross-repo change."

3. **andy-containers event fan-out (Epic AP6)**: Consumes the NDJSON event stream per `schemas/headless-events.v1.json:5` — "Consumed by andy-containers' AP6 fan-out to `andy.containers.events.run.{run_id}.progress`."

4. **andy-containers Run entity**: Correlated via `run_id` per `schemas/headless-config.v1.json:26` — "Correlates stdout events and artifacts with the andy-containers Run entity (rivoli-ai/andy-containers#103)."

5. **andy-containers signal handling**: `src/Andy.Cli/Program.cs:89-92` — "AQ5: cancel protocol... andy-containers sends SIGTERM (or SIGINT for Ctrl+C)"

6. **andy-containers-cli**: Used as a `cli` transport tool in the sample config `schemas/samples/coding-headless.json:34-35` — `binary: "andy-containers-cli"`, `command: ["andy-containers-cli", "exec", "--"]`.

7. **andy-containers configurator populates permissions**: `schemas/headless-config.v1.json:187` — "Populated by AX.9/andy-containers from the resolved policy."

### Sample Config Endpoint Patterns

All three sample configs use hardcoded absolute URLs (no `$ANDY_MCP_URL` substitution), demonstrating the expectation that the configurator resolves this before writing:
- `coding-headless.json`: `"endpoint": "https://mcp.internal/tools/fs.patch"`, `"endpoint": "https://mcp.internal/tools/container.exec"`
- `planning-headless.json`: `"endpoint": "https://mcp.internal/tools/docs.search"`, `"endpoint": "https://mcp.internal/tools/docs.put"`
- `triage-headless.json`: `"endpoint": "https://mcp.internal/tools/issues.get"`

None use `$ANDY_MCP_URL/<tool-name>` templating — they're already resolved.

## 3. Unknowns

- **AQ6 does not exist in this repo**. The search for "AQ6" across all `.cs`, `.md`, and `.json` files returned zero results. If AQ6 refers to a planned/external specification (e.g., in the conductor/andy-containers repo), it is outside the scope of this codebase. This would need to be resolved by checking the `rivoli-ai/andy-containers` or `rivoli-ai/conductor` repos.
- **Does the andy-containers configurator actually resolve `$ANDY_MCP_URL` into endpoint fields?** This is the expected behavior per the docs, but the andy-containers source is not in this repo. The sample configs show fully resolved URLs, which is consistent.
- **What is the runtime value of `$ANDY_MCP_URL` in production containers?** Not discoverable from this repo; would require inspecting the container runtime's environment injection.
- **Is `ANDY_PROXY_URL` ever read?** Same pattern as `ANDY_MCP_URL` — documented as reserved but no C# source reads it. Likely consumed by the MCP transport layer or proxy configuration outside this repo.

## 4. Risks

- **Phantom contract**: `$ANDY_MCP_URL` is documented as a "reserved" env var that "the container runtime always sets," yet the CLI runtime never reads it. If someone assumes the CLI expands `$ANDY_MCP_URL` references in endpoint fields, their config will fail at `HeadlessToolHost.ConnectMcpAsync` with a connection error, not a clear "env var not expanded" message.
- **Silent configurator drift**: The contract between the configurator (andy-containers) and the CLI runtime is purely the JSON schema. If the configurator produces an endpoint that doesn't match what the MCP gateway serves, the hard error at `HeadlessToolHost.cs:87-89` fires — but only at runtime, not at config-validation time. The schema has `format: "uri"` which catches malformed URIs but not mismatched routing.
- **Exit code contract fragility**: `HeadlessExitCode` numeric values are load-bearing across repos. The `docs/headless-runtime.md:208-209` states: "andy-containers, which keys off these values for retry/cancel/report semantics, so the numeric values are load-bearing and must not be reordered." Any reordering would silently break andy-containers retry/cancel logic.
- **env_vars shadowing**: The schema description warns "Reserved: ANDY_PROXY_URL, ANDY_TOKEN, ANDY_MCP_URL are always set by the container runtime (Epic Y5)" but the runtime only enforces this for `ANDY_TOKEN` (`HeadlessToolHost.cs:22-23` comment: "env_vars MUST NOT shadow this"). There is no runtime guard preventing a config from overriding `ANDY_MCP_URL` via `env_vars` — but since `ANDY_MCP_URL` is never read, this is currently harmless.
- **CliSubprocessTool security model**: Relies on `ProcessStartInfo.ArgumentList` (no shell expansion) per andy-containers hardening (referenced at `CliSubprocessTool.cs:16` — "rivoli-ai/andy-containers#139, #140"). If this code path were ever refactored to use a string-based argv, shell metacharacter injection becomes possible.

## 5. Recommendation

**Go** — the architecture is sound. `$ANDY_MCP_URL` is documented as a configurator-side variable (resolved by andy-containers at config-generation time, not by the CLI at runtime), which is consistent with the code. The `ANDY_TOKEN` is correctly enforced at the runtime level. The one improvement to flag: adding a defensive comment or schema-level note clarifying that `$ANDY_MCP_URL` is **only** a configurator-side variable (never consumed by the CLI runtime) would prevent future confusion. No code change is needed; this is a documentation clarity gap, not a functional defect.