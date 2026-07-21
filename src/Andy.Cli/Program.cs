using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tui.Backend.Terminal;
using Andy.Cli.Hosting;
using Andy.Cli.Input;
using Andy.Cli.Instrumentation;
using Andy.Cli.Widgets;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Cli.Services.Prompts;
using Andy.Cli.Tools;
using Andy.Llm;
using Andy.Llm.Extensions;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Permissions.Model;
using Andy.Tools;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli;

enum ScrollMode
{
    Feed,           // Scrolling through conversation feed
    PromptHistory   // Scrolling through prompt history
}

class Program
{
    // Minimum terminal dimensions to prevent crashes
    private const int MIN_WIDTH = 40;
    private const int MIN_HEIGHT = 10;

    // Git info cache
    private static string? _gitBranch;
    private static string? _gitCommit;

    private static (string branch, string commit) GetGitInfo()
    {
        if (_gitBranch == null || _gitCommit == null)
        {
            try
            {
                // Get current branch
                var branchProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "branch --show-current",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _gitBranch = branchProcess?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
                branchProcess?.WaitForExit();

                // Get short commit hash
                var commitProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --short HEAD",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _gitCommit = commitProcess?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
                commitProcess?.WaitForExit();
            }
            catch
            {
                _gitBranch = "unknown";
                _gitCommit = "unknown";
            }
        }
        return (_gitBranch, _gitCommit);
    }

    // AQ5 (rivoli-ai/andy-cli#50): cancel protocol for headless runs. When
    // andy-containers sends SIGTERM (or SIGINT for Ctrl+C), cancel a CTS so
    // HeadlessRunner.RunAsync follows its graceful shutdown path (flush events,
    // exit code 3, no partial output) instead of being killed abruptly. The
    // wall-clock timeout is handled separately inside HeadlessAgentRunner.
    private static async Task<Andy.Cli.HeadlessConfig.HeadlessExitCode> RunHeadlessAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();

        void HandleSignal(System.Runtime.InteropServices.PosixSignalContext context)
        {
            // Suppress the runtime's default abrupt termination so our graceful
            // shutdown path runs; cancellation flows through HeadlessRunner.
            context.Cancel = true;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Run already completed and disposed the CTS; nothing to cancel.
            }
        }

        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, HandleSignal);
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, HandleSignal);

        return await Andy.Cli.HeadlessConfig.HeadlessRunner.RunAsync(args, ct: cts.Token);
    }

    static async Task Main(string[] args)
    {
        // Capture any otherwise-unlogged crash (TUI render loop, background tasks, etc.) to
        // ~/.andy/logs/crash.log so a NullReferenceException isn't reduced to a bare message.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception uex)
            {
                Andy.Cli.Services.CrashLog.Write("AppDomain.UnhandledException", uex);
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Andy.Cli.Services.CrashLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Debug: Check if ANDY_DEBUG_RAW is set
        var debugRawEnv = Environment.GetEnvironmentVariable("ANDY_DEBUG_RAW");
        if (!string.IsNullOrEmpty(debugRawEnv))
        {
            var debugCheckFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".andy",
                "diagnostics",
                "ENV_CHECK.txt"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(debugCheckFile)!);
            File.WriteAllText(debugCheckFile, $"Program.Main() at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nANDY_DEBUG_RAW = '{debugRawEnv}'\nArgs: {string.Join(", ", args)}\n");
        }

        // Select the execution mode from the arguments. The branch order is
        // preserved in CliModeSelector (version -> acp -> headless -> command ->
        // interactive), so bare words like "version" and "run" are still matched
        // ahead of the generic non-dash "command" branch.
        switch (CliModeSelector.Select(args))
        {
            case CliMode.Version:
                // Print version and exit (non-interactive). Used by the release smoke test to verify the
                // built binary reports the version it was published as. Handled here because "--version"
                // starts with '-' and would otherwise fall through to interactive TUI mode.
                Console.WriteLine($"andy-cli {VersionInfo.ResolveDisplayVersion()}");
                return;

            case CliMode.Acp:
                await RunAcpServerModeAsync();
                return;

            case CliMode.Headless:
                // AQ2: `andy-cli run --headless --config <path>` — non-interactive.
                // Handled here (not via HandleCommandLineArgs) because it needs the
                // structured exit-code contract from rivoli-ai/andy-cli#47, which
                // doesn't fit the ICommand Success/Fail → exit 0|1 scheme.
                var exitCode = await RunHeadlessAsync(args);
                Environment.Exit((int)exitCode);
                return;

            case CliMode.Command:
                // Non-TUI one-shot commands (model, tools, permissions, help, ...).
                await HandleCommandLineArgs(args);
                return;

            case CliMode.Interactive:
            default:
                // Fall through to the interactive TUI setup below.
                break;
        }
        // Apply the persisted theme (falling back to an ANDY_THEME env default,
        // then the built-in dark theme) before the first frame is rendered.
        var themeMemory = new ThemeMemoryService();
        var savedThemeName = themeMemory.LoadTheme()
            ?? Environment.GetEnvironmentVariable("ANDY_THEME");
        var savedTheme = Andy.Cli.Themes.Theme.Resolve(savedThemeName, themeMemory.LoadTransparentBackground());
        if (savedTheme != null)
        {
            Andy.Cli.Themes.Theme.Current = savedTheme;
        }

        var caps = Andy.Tui.Backend.Terminal.CapabilityDetector.DetectFromEnvironment();

        // Ensure minimum viewport size to prevent crashes
        const int MIN_WIDTH = 40;
        const int MIN_HEIGHT = 10;
        var viewport = (Width: Math.Max(MIN_WIDTH, Console.WindowWidth), Height: Math.Max(MIN_HEIGHT, Console.WindowHeight));
        var scheduler = new Andy.Tui.Core.FrameScheduler(targetFps: 30);
        var hud = new Andy.Tui.Observability.HudOverlay { Enabled = false };
        scheduler.SetMetricsSink(hud);
        var pty = new LocalStdoutPty();
        Console.Write("\u001b[?1049h\u001b[?25l\u001b[?7l");

        // Use the terminal's own background (do not force black) so a transparent
        // or themed terminal shows through the UI. ESC[49m resets to the default bg.
        Console.ResetColor();
        Console.Write("[49m");
        Console.Clear();

        // Switch the terminal into raw byte mode for keyboard decoding. Returns
        // null (and we fall back to Console.ReadKey) when input is redirected or
        // stty is unavailable. Mouse reporting uses TryStart's default (ON) so the
        // mouse wheel scrolls the feed out of the box. To select text while capture
        // is on, hold Option (macOS) / Shift (xterm) and drag; or press F3 to turn
        // capture off for plain click-drag native selection. The default is pinned by
        // MouseDefaultRegressionTests.
        var rawInput = RawTerminalInput.TryStart();

        // Declared outside the try so it can be disposed deterministically in the
        // finally below. Remains null unless instrumentation is explicitly enabled.
        InstrumentationServer? instrumentationServer = null;

        try
        {
            bool running = true;

            // Blocking read of a single key, sourced from whichever input path is
            // active. Used by modal loops (e.g. the exit dialog) that need to wait
            // for one keypress. Wheel events are ignored while a modal is up.
            ConsoleKeyInfo ReadKeyBlocking()
            {
                if (rawInput == null) return Console.ReadKey(true);
                while (true)
                {
                    if (rawInput.TryDequeue(out var ev))
                    {
                        if (ev.Kind == TerminalInputKind.Key) return ev.Key;
                    }
                    else
                    {
                        Thread.Sleep(8);
                    }
                }
            }

            // Scroll mode state
            ScrollMode scrollMode = ScrollMode.Feed;
            int lastReflowSig = int.MinValue; // forces a full clear+repaint on the first frame
            var promptHistory = new List<string>(); // Store user prompts for history navigation
            int historyIndex = -1; // -1 means not navigating history, showing current input

            var hints = new KeyHintsBar();

            // Rebuild the key-hints bar for the current scroll mode and tool-output state.
            // Centralized so the Ctrl+O expand/collapse hint stays in sync wherever the
            // hints are refreshed (initial render, mode switches, and the Ctrl+O toggle).
            void UpdateHints()
            {
                bool mouseOn = rawInput?.MouseEnabled ?? false;
                hints.SetHints(FooterHints.Build(
                    scrollMode == ScrollMode.PromptHistory, ToolOutputView.Expanded, mouseOn));
            }

            UpdateHints();
            var toast = new Toast(); // Don't show initial toast as it interferes with prompt
            var tokenCounter = new TokenCounter();
            var contextStatusBar = new ContextStatusBar();
            var statusMessage = new StatusMessage();
            var prompt = new PromptLine();
            bool isProcessingMessage = false; // Track if we're processing a message
            prompt.SetBorder(true);
            prompt.SetShowCaret(true);
            prompt.SetFocused(true);
            var feed = new FeedView();
            feed.SetFocused(false);
            feed.SetAnimationSpeed(8); // faster scroll-in

            // Initialize inline command help for slash commands
            var inlineCommandHelp = new InlineCommandHelp();
            inlineCommandHelp.SetCommands(new[]
            {
                new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage AI models (list, switch, info, test)", Aliases = new[] { "m" } },
                new InlineCommandHelp.CommandInfo { Name = "tools", Description = "Manage and list available tools", Aliases = new[] { "tool", "t" } },
                new InlineCommandHelp.CommandInfo { Name = "theme", Description = "List, switch, or toggle transparency of the UI theme", Aliases = new[] { "themes" } },
                new InlineCommandHelp.CommandInfo { Name = "clear", Description = "Clear conversation history", Aliases = Array.Empty<string>() },
                new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help information", Aliases = new[] { "?" } },
                new InlineCommandHelp.CommandInfo { Name = "exit", Description = "Exit the application", Aliases = new[] { "quit", "bye" } }
            });

            // Initialize the tool execution tracker with the feed view
            ToolExecutionTracker.Instance.SetFeedView(feed);
            feed.AddMarkdownRich("**Ready to assist!** What can I help you learn or explore today?");

            // Initialize Andy.Llm and Andy.Tools services
            var services = new ServiceCollection();

            // Add configuration from appsettings.json
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(builder =>
            {
                // Disable console logging to avoid UI interference
                // To enable debugging, set environment variable ANDY_DEBUG=true
                if (Environment.GetEnvironmentVariable("ANDY_DEBUG") == "true")
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                }
            }); // Conditional logging based on environment variable

            // Configure LLM services: env vars first (as fallback defaults), then
            // appsettings.json Llm section (takes precedence, overwrites env var defaults)
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(configuration);

            // Expand ${...} environment variable placeholders in provider API keys
            // and auto-detect the default provider
            services.Configure<Andy.Llm.Configuration.LlmOptions>(options =>
            {
                foreach (var config in options.Providers.Values)
                {
                    if (!string.IsNullOrEmpty(config.ApiKey) && config.ApiKey.StartsWith("${") && config.ApiKey.EndsWith("}"))
                    {
                        var envVar = config.ApiKey.Substring(2, config.ApiKey.Length - 3);
                        config.ApiKey = Environment.GetEnvironmentVariable(envVar) ?? "";
                    }
                }

                // Only auto-detect if no DefaultProvider was explicitly configured in appsettings
                var configuredDefault = configuration.GetSection("Llm:DefaultProvider").Value;
                if (string.IsNullOrEmpty(configuredDefault))
                {
                    var detectionService = new ProviderDetectionService();
                    var detectedProvider = detectionService.DetectDefaultProvider();
                    if (!string.IsNullOrEmpty(detectedProvider))
                        options.DefaultProvider = detectedProvider;
                }
            });

            // JSON repair still available if needed elsewhere
            services.AddSingleton<IJsonRepairService, JsonRepairService>();
            // Remove custom parsers/renderers; rely on andy-llm structured outputs
            // services.AddSingleton<StreamingToolCallAccumulator>();
            // services.AddSingleton<IQwenResponseParser, SimpleQwenParser>();
            // services.AddSingleton<IToolCallValidator, ToolCallValidator>();
            // services.AddTransient<Andy.Cli.Parsing.Compiler.LlmResponseCompiler>();
            // services.AddTransient<Andy.Cli.Parsing.Rendering.AstRenderer>();

            // Broker for handing tool-permission requests from the agent's background task to the
            // main input-owning loop (which renders the modal). Same instance used by DI and the loop.
            var permissionBroker = new Andy.Cli.Services.PermissionRequestBroker();

            // Configure the core Andy.Tools service graph (interactive prompt for the TUI).
            // Shared with the ACP and one-shot command paths via AppCompositionRoot.
            AppCompositionRoot.AddCoreToolServices(services, permissionBroker);

            // Register code indexing service as a singleton only. The app builds a plain
            // ServiceProvider (no HostedService startup), so an AddHostedService registration would
            // never run - and if it did, it would index Directory.GetCurrentDirectory() (the wrong
            // tree for headless runs). Indexing is instead driven lazily and per-workspace by
            // CodeIndexTool, which indexes the tool execution context's WorkingDirectory on demand.
            services.AddSingleton<CodeIndexingService>();

            // Register the Andy.CodeIndex library's DB-free analysis/chunking services
            // (Roslyn-based) so they are available in-process for semantic indexing.
            services.AddSingleton<Andy.CodeIndex.Application.Interfaces.ICodeAnalysisService, Andy.CodeIndex.Infrastructure.Services.CodeAnalysisService>();
            services.AddSingleton<Andy.CodeIndex.Application.Interfaces.IChunkingService, Andy.CodeIndex.Infrastructure.Services.ChunkingService>();

            var serviceProvider = services.BuildServiceProvider();

            // Initialize tool registry and register tools
            var toolRegistry = AppCompositionRoot.InitializeToolRegistry(serviceProvider);

            // Initialize commands
            var modelCommand = new ModelCommand(serviceProvider);
            var toolsCommand = new ToolsCommand(serviceProvider);
            var permissionsCommand = new PermissionsCommand(serviceProvider);
            var permissionsManager = new Andy.Cli.Widgets.PermissionsManager(Directory.GetCurrentDirectory());
            var themeCommand = new ThemeCommand(themeMemory);
            var commandPalette = new CommandPalette();

            Andy.Model.Llm.ILlmProvider? llmProvider = null;
            SimpleAssistantService? aiService = null;

            // Build comprehensive system prompt
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDY_STRICT_ERRORS")))
            {
                Environment.SetEnvironmentVariable("ANDY_STRICT_ERRORS", "1");
            }

            var availableTools = toolRegistry.GetTools(enabledOnly: true);
            var currentModel = modelCommand.GetCurrentModel();
            var currentProvider = modelCommand.GetCurrentProvider();
            var systemPrompt = BuildSystemPrompt(availableTools, currentModel, currentProvider);

            // Conversation will be managed internally by AssistantService

            // Instrumentation exposes live agent activity (user messages, model
            // responses, tool parameters and results) over a local HTTP/SSE endpoint,
            // so it is DISABLED BY DEFAULT and only started when explicitly opted in via
            // ANDY_INSTRUMENTATION=1 or the --instrumentation flag. When enabled it binds
            // to loopback only, requires a per-process token, and redacts sensitive
            // fields unless ANDY_INSTRUMENTATION_SENSITIVE / --instrumentation-include-sensitive
            // is set. Started up front so the dashboard is reachable even when provider
            // setup below fails (e.g. a missing API key).
            var instrumentationOptions = InstrumentationOptions.FromEnvironmentAndArgs(args);
            if (instrumentationOptions.Enabled)
            {
                var instrumentationLogger = serviceProvider.GetService<ILogger<SimpleAssistantService>>();
                instrumentationServer = new InstrumentationServer(instrumentationOptions, instrumentationLogger);
                if (instrumentationServer.Start())
                {
                    // Print the tokenized URL only to the local console/feed.
                    feed.AddMarkdownRich($"[instrumentation] Real-time dashboard available at {instrumentationServer.DashboardUrl}");
                    if (instrumentationOptions.IncludeSensitive)
                    {
                        feed.AddMarkdownRich("[instrumentation] WARNING: sensitive content (messages, responses, tool I/O) is included in the stream");
                    }
                }
                else
                {
                    // Binding failed (e.g. port in use). Do NOT advertise a dead link.
                    feed.AddMarkdownRich(ConsoleColors.NotePrefix($"[instrumentation] Could not start dashboard on port {instrumentationOptions.Port} (port unavailable); instrumentation disabled"));
                    instrumentationServer.Dispose();
                    instrumentationServer = null;
                }
            }

            try
            {
                // Get the appropriate provider based on configuration
                var providerFactory = serviceProvider.GetService<Andy.Llm.Providers.ILlmProviderFactory>();
                if (providerFactory != null)
                {
                    llmProvider = providerFactory.CreateProvider(currentProvider);
                }

                if (llmProvider == null)
                {
                    throw new InvalidOperationException($"Could not create LLM provider for {currentProvider}");
                }

                var toolExecutor = serviceProvider.GetRequiredService<IToolExecutor>();
                var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                aiService = new SimpleAssistantService(
                    llmProvider,
                    toolRegistry,
                    toolExecutor,
                    feed,
                    currentModel,
                    currentProvider,
                    tokenCounter,
                    loggerFactory,
                    extraBody: Andy.Cli.Configuration.ProviderExtraBody.Resolve(configuration, currentProvider));

                var providerUrl = ProviderUrlResolver.Resolve(currentProvider);

                feed.AddMarkdownRich($"[model] {currentModel} with {currentProvider} provider [{providerUrl}] (tool-enabled)");
            }
            catch (Exception ex)
            {
                feed.AddMarkdown(ConsoleColors.ErrorPrefix($"Error: {ex.Message}"));
                if (ex.InnerException != null)
                {
                    feed.AddMarkdown(ConsoleColors.ErrorPrefix($"Inner: {ex.InnerException.Message}"));
                }
                feed.AddMarkdownRich(ConsoleColors.NotePrefix("Set CEREBRAS_API_KEY to enable AI responses"));

                // Try to at least get the LLM provider without tools for basic chat
                try
                {
                    var factory = serviceProvider.GetService<Andy.Llm.Providers.ILlmProviderFactory>();
                    if (factory != null)
                    {
                        llmProvider = factory.CreateProvider(currentProvider);
                    }
                }
                catch
                {
                    // Ignore secondary error
                }
            }

            // Setup command palette commands
            commandPalette.SetCommands(new[]
            {
                new CommandPalette.CommandItem
                {
                    Name = "Exit",
                    Description = "Quit the application",
                    Category = "General",
                    Aliases = new[] { "quit", "exit", "bye", "q" },
                    AsyncAction = async args =>
                    {
                        if (await ShowExitConfirmationAsync())
                        {
                            running = false;
                        }
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "List Models",
                    Description = "Show available AI models",
                    Category = "Model",
                    Aliases = new[] { "models", "list" },
                    AsyncAction = async args =>
                    {
                        var modelListItem = await modelCommand.CreateModelListItemAsync();
                        feed.AddItem(modelListItem);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Switch Model",
                    Description = "Change AI provider/model",
                    Category = "Model",
                    Aliases = new[] { "switch", "change" },
                    RequiredParams = new[] { "provider" },
                    ParameterHint = "Example: cerebras or openai gpt-4o",
                    AsyncAction = async args =>
                    {
                        if (args.Length < 1)
                        {
                            feed.AddMarkdownRich("Usage: Switch Model <provider> [model]\nProviders: cerebras, openai, anthropic");
                        }
                        else
                        {
                            var result = await modelCommand.ExecuteAsync(new[] { "switch" }.Concat(args).ToArray());
                            feed.AddMarkdownRich(result.Message);
                            if (result.Success)
                            {
                                // Get new provider from factory
                                var providerFactory = serviceProvider.GetService<Andy.Llm.Providers.ILlmProviderFactory>();
                                if (providerFactory != null)
                                {
                                    var newProviderName = modelCommand.GetCurrentProvider();
                                    llmProvider = providerFactory.CreateProvider(newProviderName);
                                }

                                // Rebuild system prompt for the new model
                                var newModel = modelCommand.GetCurrentModel();
                                var newProvider = modelCommand.GetCurrentProvider();
                                systemPrompt = BuildSystemPrompt(availableTools, newModel, newProvider);
                                // System prompt will be managed by the new AssistantService
                                
                                // Update AI service with new provider
                                if (llmProvider != null)
                                {
                                    // Dispose old service before creating new one
                                    aiService?.Dispose();

                                    var toolExecutor = serviceProvider.GetRequiredService<IToolExecutor>();
                                    var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                                    aiService = new SimpleAssistantService(
                                        llmProvider,
                                        toolRegistry,
                                        toolExecutor,
                                        feed,
                                        newModel,
                                        newProvider,
                                        tokenCounter,
                                        loggerFactory,
                                        extraBody: Andy.Cli.Configuration.ProviderExtraBody.Resolve(configuration, newProvider));
                                }

                                feed.AddMarkdownRich($"*Note: Conversation context reset for {modelCommand.GetCurrentProvider()} model*");
                            }
                        }
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Model Info",
                    Description = "Show current model details",
                    Category = "Model",
                    Aliases = new[] { "info", "current" },
                    AsyncAction = async args =>
                    {
                        var result = await modelCommand.ExecuteAsync(new[] { "info" });
                        feed.AddMarkdownRich(result.Message);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Test Model",
                    Description = "Test current model",
                    Category = "Model",
                    Aliases = new[] { "test" },
                    AsyncAction = async args =>
                    {
                        var result = await modelCommand.ExecuteAsync(new[] { "test" }.Concat(args).ToArray());
                        feed.AddMarkdownRich(result.Message);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "List Tools",
                    Description = "Show available AI tools",
                    Category = "Tools",
                    Aliases = new[] { "tools", "tool list" },
                    Action = args =>
                    {
                        var toolListItem = toolsCommand.CreateToolListItem();
                        feed.AddItem(toolListItem);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Tool Info",
                    Description = "Show details about a specific tool",
                    Category = "Tools",
                    Aliases = new[] { "tool info", "tool details" },
                    RequiredParams = new[] { "tool_id_or_name" },
                    ParameterHint = "Enter tool ID (e.g., read_file, copy_file) or name (e.g., \"Copy File\")",
                    GetAvailableOptions = () =>
                    {
                        // Get all available tool IDs from the registry
                        var registry = toolsCommand.GetToolRegistry();
                        if (registry != null)
                        {
                            return registry.Tools
                                .OrderBy(t => t.Metadata.Category)
                                .ThenBy(t => t.Metadata.Name)
                                .Select(t => $"{t.Metadata.Id} - {t.Metadata.Name}")
                                .ToArray();
                        }
                        return Array.Empty<string>();
                    },
                    AsyncAction = async args =>
                    {
                        if (args.Length < 1)
                        {
                            feed.AddMarkdownRich("Usage: Tool Info <tool_id_or_name>\n\nExamples:\n- copy_file\n- \"Copy File\"\n- json_processor\n\nUse /tools list to see all available tool IDs.");
                        }
                        else
                        {
                            var result = await toolsCommand.ExecuteAsync(new[] { "info" }.Concat(args).ToArray());
                            feed.AddMarkdownRich(result.Message);
                        }
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Execute Tool",
                    Description = "Run a tool with parameters",
                    Category = "Tools",
                    Aliases = new[] { "tool exec", "tool run" },
                    RequiredParams = new[] { "tool_id", "params..." },
                    ParameterHint = "Example: read_file file_path=/etc/hosts",
                    GetAvailableOptions = () =>
                    {
                        // Get all available tool IDs from the registry
                        var registry = toolsCommand.GetToolRegistry();
                        if (registry != null)
                        {
                            return registry.Tools
                                .OrderBy(t => t.Metadata.Category)
                                .ThenBy(t => t.Metadata.Name)
                                .Select(t => $"{t.Metadata.Id} - {t.Metadata.Name}")
                                .ToArray();
                        }
                        return Array.Empty<string>();
                    },
                    AsyncAction = async args =>
                    {
                        if (args.Length < 1)
                        {
                            feed.AddMarkdownRich("Usage: Execute Tool <tool_name> [parameters...]");
                        }
                        else
                        {
                            var result = await toolsCommand.ExecuteAsync(new[] { "execute" }.Concat(args).ToArray());
                            feed.AddMarkdownRich(result.Message);
                        }
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Clear Chat",
                    Description = "Clear conversation history",
                    Category = "Chat",
                    Aliases = new[] { "clear", "reset" },
                    Action = args =>
                    {
                        // Clear conversation context and reset token counter
                        if (aiService != null)
                        {
                            aiService.ClearContext();
                        }
                        tokenCounter.Reset();
                        feed.Clear();
                        feed.AddMarkdownRich("**Chat cleared!** Ready for a fresh conversation.");
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Toggle HUD",
                    Description = "Show/hide performance overlay",
                    Category = "UI",
                    Aliases = new[] { "hud", "debug" },
                    Action = args =>
                    {
                        hud.Enabled = !hud.Enabled;
                        toast.Show(hud.Enabled ? "HUD enabled" : "HUD disabled", 60);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Switch Theme",
                    Description = "List or change the UI theme",
                    Category = "UI",
                    Aliases = new[] { "theme", "themes" },
                    RequiredParams = new[] { "theme" },
                    ParameterHint = "Example: dark or light",
                    GetAvailableOptions = () => Andy.Cli.Themes.Theme.AvailableThemes.ToArray(),
                    AsyncAction = async args =>
                    {
                        var result = await themeCommand.ExecuteAsync(args);
                        feed.AddMarkdownRich(result.Message);
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "Help",
                    Description = "Show help information",
                    Category = "General",
                    Aliases = new[] { "?", "help" },
                    Action = args =>
                    {
                        feed.AddMarkdownRich("# Andy CLI Help\n\n" +
                            "## Keyboard Shortcuts:\n" +
                            "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
                            "- **Ctrl+]**: Toggle scroll mode (Feed ↔ Prompt History)\n" +
                            "- **Ctrl+D**: Quit application\n" +
                            "- **F2**: Toggle HUD (performance overlay)\n" +
                            "- **F3**: Toggle mouse capture (ON by default = mouse-wheel scroll; Option+drag to select text, Cmd+C to copy. F3 OFF = plain-drag native selection)\n" +
                            "- **Ctrl+O**: Expand/collapse tool output detail (view-only; does not affect a running turn)\n" +
                            "- **ESC**: Quit application\n" +
                            "- **Page Up/Down**: Scroll chat history\n" +
                            "- **↑/↓**: Navigate multi-line text or prompt history (when in History mode)\n" +
                            "- **Ctrl+A/E**: Jump to start/end of current line\n" +
                            "- **Home/End**: Start/end of line (Ctrl: whole text)\n" +
                            "- **Ctrl+K**: Delete from cursor to end of line\n" +
                            "- **Ctrl+U**: Delete from start of line to cursor\n\n" +
                            "## Scroll Modes:\n" +
                            "- **Feed Mode** (default): Blue indicator on left. PageUp/PageDown scrolls conversation.\n" +
                            "- **Prompt History Mode**: Orange indicator on left. ↑/↓ navigates previous messages. Shows message counter (e.g., 5/12).\n\n" +
                            "## Commands:\n" +
                            "### General Commands:\n" +
                            "- **/exit**, **/bye**, **/quit**: Exit the application\n" +
                            "- **exit**, **bye**, **quit**: Exit the application (without slash)\n" +
                            "- **/clear**: Clear conversation history\n" +
                            "- **/help**: Show this help message\n\n" +
                            "### Model Commands:\n" +
                            "- **/model list**: Show available models\n" +
                            "- **/model switch <provider>**: Change provider\n" +
                            "- **/model info**: Show current model details\n" +
                            "- **/model test [prompt]**: Test current model\n\n" +
                            "### Theme Commands:\n" +
                            "- **/theme**: List available themes and the current one\n" +
                            "- **/theme <name>**: Switch the UI theme (e.g. dark, dracula, nord)\n" +
                            "- **/theme transparent on|off**: Toggle the transparent background\n\n" +
                            "### Tool Commands:\n" +
                            "- **/tools list [category]**: List available tools\n" +
                            "- **/tools info <tool_name>**: Show tool details\n" +
                            "- **/tools execute <tool_name> [params]**: Run a tool\n\n" +
                            "### Permission Commands:\n" +
                            "- **/permissions**: List effective permission rules by layer\n" +
                            "- **/permissions allow|ask|deny <tool[(spec)]> [--scope user|project|local]**: Persist a rule\n" +
                            "- **/permissions path**: Show the rule file locations\n\n" +
                            "## Providers:\n" +
                            "- **cerebras**: Fast Llama models\n" +
                            "- **openai**: GPT-4 models\n" +
                            "- **anthropic**: Claude models");
                    }
                }
            });

            bool cursorStyledShown = false;
            var lastWidth = viewport.Width;
            var lastHeight = viewport.Height;

            // Helper method to show exit confirmation dialog
            async Task<bool> ShowExitConfirmationAsync()
            {
                bool confirmExit = false;
                bool dialogOpen = true;
                bool yesSelected = false; // Default to No

                while (dialogOpen)
                {
                    var confirmB = new DL.DisplayListBuilder();
                    confirmB.PushClip(new DL.ClipPush(0, 0, viewport.Width, viewport.Height));

                    // Opaque backdrop: occlude the content behind the modal dialog.
                    confirmB.DrawRect(new DL.Rect(0, 0, viewport.Width, viewport.Height, new DL.Rgb24(0, 0, 0)));

                    // Dialog box
                    int bw = Math.Min(44, viewport.Width - 4);
                    int bh = 7;
                    int bx = (viewport.Width - bw) / 2;
                    int by = (viewport.Height - bh) / 2;

                    // Draw dialog background and border
                    confirmB.PushClip(new DL.ClipPush(bx, by, bw, bh));
                    confirmB.DrawRect(new DL.Rect(bx, by, bw, bh, new DL.Rgb24(30, 30, 40)));
                    confirmB.DrawBorder(new DL.Border(bx, by, bw, bh, "double", new DL.Rgb24(200, 200, 80)));

                    // Title
                    string title = "Exit Application?";
                    int titleX = bx + (bw - title.Length) / 2;
                    confirmB.DrawText(new DL.TextRun(titleX, by + 2, title, new DL.Rgb24(255, 255, 255), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.Bold));

                    // Buttons
                    int buttonY = by + 4;
                    int buttonSpacing = 12;
                    int noButtonX = bx + (bw / 2) - buttonSpacing;
                    int yesButtonX = bx + (bw / 2) + 3;

                    // No button (default)
                    var noBg = !yesSelected ? new DL.Rgb24(60, 120, 60) : new DL.Rgb24(40, 40, 50);
                    var noFg = !yesSelected ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(180, 180, 180);
                    confirmB.DrawRect(new DL.Rect(noButtonX, buttonY, 8, 1, noBg));
                    confirmB.DrawText(new DL.TextRun(noButtonX + 1, buttonY, !yesSelected ? "[ No ]" : "  No  ", noFg, noBg, !yesSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));

                    // Yes button
                    var yesBg = yesSelected ? new DL.Rgb24(120, 60, 60) : new DL.Rgb24(40, 40, 50);
                    var yesFg = yesSelected ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(180, 180, 180);
                    confirmB.DrawRect(new DL.Rect(yesButtonX, buttonY, 8, 1, yesBg));
                    confirmB.DrawText(new DL.TextRun(yesButtonX + 1, buttonY, yesSelected ? "[ Yes ]" : "  Yes  ", yesFg, yesBg, yesSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));

                    // Hints
                    string hints = "← → Navigate  Enter Select  Esc Cancel";
                    int hintsX = bx + (bw - hints.Length) / 2;
                    confirmB.DrawText(new DL.TextRun(hintsX, by + bh - 1, hints, new DL.Rgb24(120, 120, 150), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.None));

                    confirmB.Pop();
                    await scheduler.RenderOnceAsync(confirmB.Build(), viewport, caps, pty, CancellationToken.None);

                    // Handle input
                    ConsoleKeyInfo k2 = ReadKeyBlocking();
                    if (k2.Key == ConsoleKey.LeftArrow || k2.Key == ConsoleKey.RightArrow || k2.Key == ConsoleKey.Tab)
                    {
                        yesSelected = !yesSelected;
                    }
                    else if (k2.Key == ConsoleKey.Enter || k2.Key == ConsoleKey.Spacebar)
                    {
                        confirmExit = yesSelected;
                        dialogOpen = false;
                    }
                    else if (k2.Key == ConsoleKey.Escape || k2.Key == ConsoleKey.N)
                    {
                        dialogOpen = false;
                    }
                    else if (k2.Key == ConsoleKey.Y)
                    {
                        confirmExit = true;
                        dialogOpen = false;
                    }
                }

                return confirmExit;
            }

            // Modal asking the user to approve/deny a tool call. Runs on the main thread (which owns the
            // input queue and renderer), reusing the same render + blocking-keypress primitive as the exit
            // dialog. Returns the decision to hand back to the awaiting agent task.
            async Task<PermissionDecision> ShowPermissionDialogAsync(PermissionRequest request)
            {
                string[] labels = { "Allow once", "Allow (session)", "Deny" };
                int selected = 2; // default to the safe choice
                bool open = true;
                int scroll = 0; // first visible wrapped summary line

                while (open)
                {
                    var pb = new DL.DisplayListBuilder();
                    pb.PushClip(new DL.ClipPush(0, 0, viewport.Width, viewport.Height));
                    pb.DrawRect(new DL.Rect(0, 0, viewport.Width, viewport.Height, new DL.Rgb24(0, 0, 0)));

                    var panelBg = new DL.Rgb24(30, 30, 40);
                    int bw = Math.Min(Math.Max(48, viewport.Width - 4), 96);
                    int iw = Math.Max(8, bw - 4);

                    var toolLine = $"Tool: {request.ToolDisplayName ?? request.ToolId}";
                    var summaryLines = Andy.Cli.Widgets.TextWrap.Wrap(request.ActionSummary, iw);

                    // Grow the dialog to show as many summary lines as fit; scroll if longer.
                    const int chrome = 8; // borders(2) + title + blank + tool + blank + buttons + hints
                    int maxSummary = Math.Max(1, viewport.Height - 2 - chrome);
                    int summaryVisible = Math.Min(summaryLines.Count, maxSummary);
                    int maxScroll = Math.Max(0, summaryLines.Count - summaryVisible);
                    scroll = Math.Clamp(scroll, 0, maxScroll);

                    int bh = summaryVisible + chrome;
                    int bx = (viewport.Width - bw) / 2;
                    int by = Math.Max(0, (viewport.Height - bh) / 2);

                    pb.PushClip(new DL.ClipPush(bx, by, bw, bh));
                    pb.DrawRect(new DL.Rect(bx, by, bw, bh, panelBg));
                    pb.DrawBorder(new DL.Border(bx, by, bw, bh, "double", new DL.Rgb24(200, 200, 80)));

                    string title = "Permission required";
                    pb.DrawText(new DL.TextRun(bx + (bw - title.Length) / 2, by + 1, title, new DL.Rgb24(255, 255, 255), panelBg, DL.CellAttrFlags.Bold));

                    var tool = toolLine.Length <= iw ? toolLine : toolLine[..Math.Max(0, iw - 1)] + "…";
                    pb.DrawText(new DL.TextRun(bx + 2, by + 3, tool, new DL.Rgb24(220, 220, 160), panelBg, DL.CellAttrFlags.None));

                    if (maxScroll > 0)
                    {
                        string pos = $"[{scroll + 1}-{scroll + summaryVisible}/{summaryLines.Count} ↑↓]";
                        pb.DrawText(new DL.TextRun(bx + bw - 2 - pos.Length, by + 3, pos, new DL.Rgb24(150, 150, 170), panelBg, DL.CellAttrFlags.None));
                    }

                    int sy = by + 4;
                    for (int i = 0; i < summaryVisible; i++)
                    {
                        pb.DrawText(new DL.TextRun(bx + 2, sy + i, summaryLines[scroll + i], new DL.Rgb24(200, 200, 210), panelBg, DL.CellAttrFlags.None));
                    }

                    int btnY = sy + summaryVisible + 1;
                    int x = bx + 2;
                    for (int i = 0; i < labels.Length; i++)
                    {
                        bool on = i == selected;
                        var bg = on ? new DL.Rgb24(60, 90, 130) : new DL.Rgb24(40, 40, 50);
                        var fg = on ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(180, 180, 180);
                        string text = on ? $"[ {labels[i]} ]" : $"  {labels[i]}  ";
                        pb.DrawRect(new DL.Rect(x, btnY, text.Length, 1, bg));
                        pb.DrawText(new DL.TextRun(x, btnY, text, fg, bg, on ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                        x += text.Length + 2;
                    }

                    string hints = maxScroll > 0 ? "← → Select  ↑ ↓ Scroll  Enter Confirm  Esc Deny" : "← → Navigate  Enter Select  Esc Deny";
                    pb.DrawText(new DL.TextRun(bx + Math.Max(2, (bw - hints.Length) / 2), by + bh - 1, hints, new DL.Rgb24(120, 120, 150), panelBg, DL.CellAttrFlags.None));

                    pb.Pop();
                    await scheduler.RenderOnceAsync(pb.Build(), viewport, caps, pty, CancellationToken.None);

                    var k = ReadKeyBlocking();
                    if (k.Key == ConsoleKey.LeftArrow) selected = (selected + labels.Length - 1) % labels.Length;
                    else if (k.Key == ConsoleKey.RightArrow || k.Key == ConsoleKey.Tab) selected = (selected + 1) % labels.Length;
                    else if (k.Key == ConsoleKey.UpArrow) scroll -= 1;
                    else if (k.Key == ConsoleKey.DownArrow) scroll += 1;
                    else if (k.Key == ConsoleKey.PageUp) scroll -= 5;
                    else if (k.Key == ConsoleKey.PageDown) scroll += 5;
                    else if (k.Key == ConsoleKey.Escape || k.Key == ConsoleKey.D || k.Key == ConsoleKey.N) { selected = 2; open = false; }
                    else if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar) open = false;
                }

                return selected switch
                {
                    0 => new PermissionDecision(true, PersistScope.Once),
                    1 => new PermissionDecision(true, PersistScope.Session),
                    _ => new PermissionDecision(false, PersistScope.Once),
                };
            }

            while (running)
            {
                // Check for terminal resize
                if (Console.WindowWidth != lastWidth || Console.WindowHeight != lastHeight)
                {
                    // Apply minimum size constraints
                    viewport = (Width: Math.Max(MIN_WIDTH, Console.WindowWidth), Height: Math.Max(MIN_HEIGHT, Console.WindowHeight));
                    lastWidth = viewport.Width;
                    lastHeight = viewport.Height;
                    // Force a full redraw on resize
                    Console.Clear();
                }

                // Service a pending tool-permission request (posted by the agent's background task) on
                // this input-owning thread, then re-render on the next iteration.
                if (permissionBroker.TryDequeue(out var pendingPermission) && pendingPermission != null)
                {
                    var decision = await ShowPermissionDialogAsync(pendingPermission.Request);
                    pendingPermission.Completion.TrySetResult(decision);
                    continue;
                }

                // Input (prefer KeyAvailable; fallback to Console.In.Peek in non-interactive contexts)
                async Task HandleKey(ConsoleKeyInfo k)
                {
                    // Handle Ctrl+D for exit
                    if (k.Key == ConsoleKey.D && (k.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        if (await ShowExitConfirmationAsync())
                        {
                            running = false;
                        }
                        return;
                    }

                    // Interactive permissions manager owns all keys while open.
                    if (permissionsManager.IsOpen)
                    {
                        switch (k.Key)
                        {
                            case ConsoleKey.Escape: permissionsManager.Close(); return;
                            case ConsoleKey.UpArrow: permissionsManager.MoveSelection(-1); return;
                            case ConsoleKey.DownArrow: permissionsManager.MoveSelection(1); return;
                            case ConsoleKey.Enter:
                            case ConsoleKey.Spacebar: permissionsManager.CycleSelectedOutcome(); return;
                            case ConsoleKey.Delete: permissionsManager.DeleteSelected(); return;
                        }
                        if (k.KeyChar is 'd' or 'D') { permissionsManager.DeleteSelected(); return; }
                        if (k.KeyChar is 'r' or 'R') { permissionsManager.Reload(); return; }
                        return; // swallow any other key while the manager is open
                    }

                    if (k.Key == ConsoleKey.Escape)
                    {
                        // If command palette is open, close it first
                        if (commandPalette.IsOpen)
                        {
                            commandPalette.Close();
                            return;
                        }

                        // Show exit confirmation dialog
                        if (await ShowExitConfirmationAsync())
                        {
                            running = false;
                        }
                        return;
                    }
                    if (k.Key == ConsoleKey.F2) { hud.Enabled = !hud.Enabled; return; }

                    // F3 toggles mouse capture. Mouse capture is off by default
                    // so the terminal's native click-drag text selection works;
                    // turning it on enables mouse-wheel scrolling of the feed at
                    // the cost of suppressing native selection. Wheel scrolling
                    // is also available via PageUp/PageDown regardless.
                    if (k.Key == ConsoleKey.F3)
                    {
                        if (rawInput == null)
                        {
                            toast.Show("Mouse capture unavailable (no raw terminal)", 90);
                        }
                        else
                        {
                            bool on = rawInput.ToggleMouseReporting();
                            toast.Show(on
                                ? "Mouse capture ON (wheel scrolls; Option+drag to select text, Cmd+C to copy)"
                                : "Mouse capture OFF (plain-drag native text selection enabled)", 120);
                            // Refresh the footer so the Mouse On/Off indicator matches the new state.
                            UpdateHints();
                        }
                        return;
                    }

                    // Ctrl+O toggles expand/collapse of tool-execution output detail.
                    //
                    // IMPORTANT: this is a PURE VIEW toggle. It only flips a shared
                    // presentation flag (ToolOutputView.Expanded) that the feed's tool
                    // items read at measure/render time. It does NOT cancel, pause, or
                    // otherwise affect the in-flight assistant turn:
                    //   - the assistant turn runs on a background Task,
                    //   - the render loop runs continuously (~60fps) on this thread,
                    //   - so flipping the flag simply re-measures and re-renders the
                    //     already-recorded tool items on the next frame.
                    // Both completed and still-running tool items pick up the new mode
                    // because they consult the flag every frame rather than at creation.
                    if (k.Key == ConsoleKey.O && (k.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        bool expanded = ToolOutputView.Toggle();
                        toast.Show(expanded
                            ? "Tool output: expanded (full params + result preview)"
                            : "Tool output: collapsed (compact summary)", 90);
                        UpdateHints();
                        return;
                    }

                    // Handle Ctrl+P / Cmd+P for command palette
                    if (k.Key == ConsoleKey.P && (k.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        if (commandPalette.IsOpen)
                        {
                            commandPalette.Close();
                        }
                        else
                        {
                            commandPalette.Open();
                        }
                        return;
                    }

                    // Handle command palette input when open
                    if (commandPalette.IsOpen)
                    {
                        // Check if we're in parameter input mode
                        if (commandPalette.IsWaitingForParams())
                        {
                            if (k.Key == ConsoleKey.UpArrow)
                            {
                                commandPalette.MoveSelection(-1);
                                return;
                            }
                            else if (k.Key == ConsoleKey.DownArrow)
                            {
                                commandPalette.MoveSelection(1);
                                return;
                            }
                            else if (k.Key == ConsoleKey.Enter)
                            {
                                // Execute with the entered parameters
                                await commandPalette.ExecuteSelectedAsync();
                                return;
                            }
                            else if (k.Key == ConsoleKey.Backspace)
                            {
                                var input = commandPalette.GetQuery();
                                if (input.Length > 0)
                                {
                                    commandPalette.SetParamInput(input.Substring(0, input.Length - 1));
                                }
                                return;
                            }
                            else if (!char.IsControl(k.KeyChar))
                            {
                                commandPalette.SetParamInput(commandPalette.GetQuery() + k.KeyChar);
                                return;
                            }
                        }
                        else
                        {
                            // Normal command selection mode
                            if (k.Key == ConsoleKey.UpArrow)
                            {
                                commandPalette.MoveSelection(-1);
                                return;
                            }
                            else if (k.Key == ConsoleKey.DownArrow)
                            {
                                commandPalette.MoveSelection(1);
                                return;
                            }
                            else if (k.Key == ConsoleKey.Enter)
                            {
                                await commandPalette.ExecuteSelectedAsync();
                                return;
                            }
                            else if (k.Key == ConsoleKey.Backspace)
                            {
                                var query = commandPalette.GetQuery();
                                if (query.Length > 0)
                                {
                                    commandPalette.SetQuery(query.Substring(0, query.Length - 1));
                                }
                                return;
                            }
                            else if (!char.IsControl(k.KeyChar))
                            {
                                commandPalette.SetQuery(commandPalette.GetQuery() + k.KeyChar);
                                return;
                            }
                        }
                        return;
                    }

                    // Handle prompt history navigation when in PromptHistory scroll mode
                    if (scrollMode == ScrollMode.PromptHistory && promptHistory.Count > 0)
                    {
                        if (k.Key == ConsoleKey.UpArrow && (k.Modifiers & ConsoleModifiers.Control) == 0)
                        {
                            // Navigate to previous message in history
                            if (historyIndex == -1)
                            {
                                // First time pressing up - go to most recent
                                historyIndex = promptHistory.Count - 1;
                            }
                            else if (historyIndex > 0)
                            {
                                historyIndex--;
                            }

                            if (historyIndex >= 0 && historyIndex < promptHistory.Count)
                            {
                                prompt.SetText(promptHistory[historyIndex]);
                            }
                            return;
                        }
                        else if (k.Key == ConsoleKey.DownArrow && (k.Modifiers & ConsoleModifiers.Control) == 0)
                        {
                            // Navigate to next message in history
                            if (historyIndex >= 0)
                            {
                                historyIndex++;
                                if (historyIndex >= promptHistory.Count)
                                {
                                    // Reached the end - clear prompt
                                    historyIndex = -1;
                                    prompt.SetText("");
                                }
                                else
                                {
                                    prompt.SetText(promptHistory[historyIndex]);
                                }
                            }
                            return;
                        }
                    }

                    // Avoid mapping regular alphanumeric keys to actions
                    var textBeforeKey = prompt.Text;
                    var submitted = prompt.OnKey(k);
                    // If the keystroke edited the prompt text (typing, paste, backspace,
                    // delete, etc.), snap the feed back to the bottom where the prompt
                    // lives. Pure navigation/scroll keys (arrows, Home/End, PgUp/PgDn,
                    // wheel) do not change the text and therefore do not yank the view.
                    if (!ReferenceEquals(submitted, null) || !string.Equals(prompt.Text, textBeforeKey, StringComparison.Ordinal))
                    {
                        feed.SnapToBottom();
                    }
                    if (submitted is string cmd && !string.IsNullOrWhiteSpace(cmd) && !isProcessingMessage)
                    {
                        // Check for slash commands
                        if (cmd.StartsWith("/"))
                        {
                            var parts = cmd.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                var commandName = parts[0].ToLowerInvariant();
                                var args = parts.Skip(1).ToArray();

                                if (commandName == "model" || commandName == "m")
                                {
                                    feed.AddUserMessage(cmd);

                                    // Check if it's a list command or no args (default to list)
                                    if (args.Length == 0 || args[0] == "list" || args[0] == "ls")
                                    {
                                        var modelListItem = await modelCommand.CreateModelListItemAsync();
                                        feed.AddItem(modelListItem);
                                    }
                                    else
                                    {
                                        var result = await modelCommand.ExecuteAsync(args);
                                        feed.AddMarkdownRich(result.Message);
                                        if (result.Success && args.Length > 0 && (args[0] == "switch" || args[0] == "sw"))
                                        {
                                            // Get new provider from factory
                                            var providerFactory = serviceProvider.GetService<Andy.Llm.Providers.ILlmProviderFactory>();
                                            if (providerFactory != null)
                                            {
                                                var newProviderName = modelCommand.GetCurrentProvider();
                                                llmProvider = providerFactory.CreateProvider(newProviderName);
                                            }

                                            // Rebuild system prompt for the new model
                                            var newModel = modelCommand.GetCurrentModel();
                                            var newProvider = modelCommand.GetCurrentProvider();
                                            systemPrompt = BuildSystemPrompt(availableTools, newModel, newProvider);
                                            // System prompt will be managed by the new AssistantService

                                            // Update AI service with new provider
                                            if (llmProvider != null)
                                            {
                                                // Dispose old service before creating new one
                                                aiService?.Dispose();

                                                var toolExecutor = serviceProvider.GetRequiredService<IToolExecutor>();
                                                var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                                                aiService = new SimpleAssistantService(
                                                    llmProvider,
                                                    toolRegistry,
                                                    toolExecutor,
                                                    feed,
                                                    newModel,
                                                    newProvider,
                                                    tokenCounter,
                                                    loggerFactory,
                                                    extraBody: Andy.Cli.Configuration.ProviderExtraBody.Resolve(configuration, newProvider));
                                            }

                                            feed.AddMarkdownRich($"*Note: Conversation context reset for {modelCommand.GetCurrentProvider()} model*");
                                        }
                                    }
                                    return;
                                }
                                else if (commandName == "tools" || commandName == "tool" || commandName == "t")
                                {
                                    feed.AddUserMessage(cmd);

                                    // Check if it's a list command or no args (default to list)
                                    if (args.Length == 0 || args[0] == "list" || args[0] == "ls")
                                    {
                                        var toolListItem = toolsCommand.CreateToolListItem();
                                        feed.AddItem(toolListItem);
                                    }
                                    else
                                    {
                                        var result = await toolsCommand.ExecuteAsync(args);
                                        feed.AddMarkdownRich(result.Message);
                                    }
                                    return;
                                }
                                else if (commandName == "permissions" || commandName == "perms" || commandName == "perm")
                                {
                                    // No args -> open the interactive manager; subcommands run the command.
                                    if (args.Length == 0 || args[0].Equals("manage", StringComparison.OrdinalIgnoreCase))
                                    {
                                        permissionsManager.Open();
                                        return;
                                    }
                                    feed.AddUserMessage(cmd);
                                    var result = await permissionsCommand.ExecuteAsync(args);
                                    // Fence the output so the layered rule list stays aligned (monospace).
                                    feed.AddMarkdownRich("```\n" + result.Message + "\n```");
                                    return;
                                }
                                else if (commandName == "help" || commandName == "?")
                                {
                                    feed.AddUserMessage(cmd);
                                    feed.AddMarkdownRich("# Andy CLI Help\n\n" +
                                        "## Keyboard Shortcuts:\n" +
                                        "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
                                        "- **Ctrl+]**: Toggle scroll mode (Feed ↔ Prompt History)\n" +
                                        "- **Ctrl+D**: Quit application\n" +
                                        "- **F2**: Toggle HUD (performance overlay)\n" +
                            "- **F3**: Toggle mouse capture (ON by default = mouse-wheel scroll; Option+drag to select text, Cmd+C to copy. F3 OFF = plain-drag native selection)\n" +
                                        "- **Ctrl+O**: Expand/collapse tool output detail (view-only; does not affect a running turn)\n" +
                                        "- **ESC**: Quit application\n" +
                                        "- **Page Up/Down**: Scroll chat history\n" +
                                        "- **↑/↓**: Navigate multi-line text or prompt history (when in History mode)\n" +
                                        "- **Ctrl+A/E**: Jump to start/end of current line\n" +
                                        "- **Home/End**: Start/end of line (Ctrl: whole text)\n" +
                                        "- **Ctrl+K**: Delete from cursor to end of line\n" +
                                        "- **Ctrl+U**: Delete from start of line to cursor\n\n" +
                                        "## Scroll Modes:\n" +
                                        "- **Feed Mode** (default): Blue indicator on left. PageUp/PageDown scrolls conversation.\n" +
                                        "- **Prompt History Mode**: Orange indicator on left. ↑/↓ navigates previous messages. Shows message counter (e.g., 5/12).\n\n" +
                                        "## Commands:\n" +
                                        "### General Commands:\n" +
                                        "- **/exit**, **/bye**, **/quit**: Exit the application\n" +
                                        "- **exit**, **bye**, **quit**: Exit the application (without slash)\n" +
                                        "- **/clear**: Clear conversation history\n" +
                                        "- **/help**: Show this help message\n\n" +
                                        "### Model Commands:\n" +
                                        "- **/model list**: Show available models\n" +
                                        "- **/model switch <provider>**: Change provider\n" +
                                        "- **/model info**: Show current model details\n" +
                                        "- **/model test [prompt]**: Test current model\n\n" +
                                        "### Theme Commands:\n" +
                                        "- **/theme**: List available themes and the current one\n" +
                                        "- **/theme <name>**: Switch the UI theme (e.g. dark, dracula, nord)\n" +
                                        "- **/theme transparent on|off**: Toggle the transparent background\n\n" +
                                        "### Tool Commands:\n" +
                                        "- **/tools list [category]**: List available tools\n" +
                                        "- **/tools info <tool_name>**: Show tool details\n" +
                                        "- **/tools execute <tool_name> [params]**: Run a tool\n\n" +
                                        "### Permission Commands:\n" +
                                        "- **/permissions**: List effective permission rules by layer\n" +
                                        "- **/permissions allow|ask|deny <tool[(spec)]> [--scope user|project|local]**: Persist a rule\n" +
                                        "- **/permissions path**: Show the rule file locations\n\n" +
                                        "## Providers:\n" +
                                        "- **cerebras**: Fast Llama models\n" +
                                        "- **openai**: GPT-4 models\n" +
                                        "- **anthropic**: Claude models\n\n" +
                                        "## Tool Categories:\n" +
                                        "- **FileSystem**: File operations\n" +
                                        "- **TextProcessing**: Text manipulation\n" +
                                        "- **System**: System information\n" +
                                        "- **Web**: HTTP and JSON tools");
                                    return;
                                }
                                else if (commandName == "theme" || commandName == "themes")
                                {
                                    feed.AddUserMessage(cmd);
                                    var result = await themeCommand.ExecuteAsync(args);
                                    feed.AddMarkdownRich(result.Message);
                                    return;
                                }
                                else if (commandName == "clear")
                                {
                                    // Clear conversation context and reset token counter
                                    if (aiService != null)
                                    {
                                        aiService.ClearContext();
                                    }
                                    tokenCounter.Reset();
                                    feed.Clear();
                                    feed.AddMarkdownRich("**Chat cleared!** Ready for a fresh conversation.");
                                    return;
                                }
                                else if (commandName == "exit" || commandName == "bye" || commandName == "quit")
                                {
                                    // Show exit confirmation dialog
                                    if (await ShowExitConfirmationAsync())
                                    {
                                        running = false;
                                    }
                                    return;
                                }
                                else
                                {
                                    feed.AddUserMessage(cmd);
                                    feed.AddMarkdownRich(ConsoleColors.WarningPrefix($"Unknown command: /{commandName}. Type /help for available commands."));
                                    return;
                                }
                            }
                        }

                        // Check for exit commands without slash
                        var trimmedCmd = cmd.Trim().ToLowerInvariant();
                        if (trimmedCmd == "exit" || trimmedCmd == "bye" || trimmedCmd == "quit")
                        {
                            // Show exit confirmation dialog
                            if (await ShowExitConfirmationAsync())
                            {
                                running = false;
                            }
                            return;
                        }

                        // Regular chat message
                        // Store in prompt history first to get the message number
                        promptHistory.Add(cmd);
                        int messageNumber = promptHistory.Count;
                        feed.AddUserMessage(cmd, messageNumber);
                        historyIndex = -1; // Reset to showing current input

                        if (aiService != null)
                        {
                            // Run the assistant processing on a background task so UI can update
                            isProcessingMessage = true;
                            prompt.SetShowCaret(false); // Hide cursor during processing
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    statusMessage.SetMessage("Thinking", animated: true);

                                    // Process message with tool support (streaming disabled until properly implemented)
                                    var response = await aiService.ProcessMessageAsync(cmd, enableStreaming: false);

                                    // Token counter is now updated in real-time by SimpleAssistantService

                                    statusMessage.SetMessage("Ready for next question", animated: false);
                                }
                                catch (Exception ex)
                                {
                                    Andy.Cli.Services.CrashLog.Write("interactive.ProcessMessageAsync", ex);
                                    feed.AddMarkdownRich(ConsoleColors.ErrorPrefix(ex.Message));
                                    feed.AddMarkdownRich(ConsoleColors.ErrorPrefix($"        (full trace: {Andy.Cli.Services.CrashLog.Path})"));
                                    statusMessage.SetMessage("Error occurred", animated: false);
                                }
                                finally
                                {
                                    isProcessingMessage = false;
                                    prompt.SetShowCaret(true); // Show cursor again when done
                                }
                            });
                        }
                        // No fallback - if aiService is null, the user needs to configure API keys
                        // The initialization error message above already informed them
                        return;
                    }
                    // Ctrl+] toggles scroll mode (checking both key code and character)
                    if (((k.Key == ConsoleKey.Oem6 || k.KeyChar == ']' || k.KeyChar == '\u001d') && (k.Modifiers & ConsoleModifiers.Control) != 0))
                    {
                        scrollMode = scrollMode == ScrollMode.Feed ? ScrollMode.PromptHistory : ScrollMode.Feed;

                        // Update hints to show current mode
                        if (scrollMode == ScrollMode.PromptHistory)
                        {
                            toast.Show($"Scroll mode: Prompt History ({promptHistory.Count} messages)", 90);
                        }
                        else
                        {
                            toast.Show("Scroll mode: Feed", 90);
                        }
                        UpdateHints();
                        return;
                    }

                    // PageUp/PageDown always scrolls the feed by exactly one page (a few lines of
                    // overlap kept for context). int.MaxValue/MinValue are FeedView's "one page"
                    // sentinels — using them keeps the page-step in one place so the view can't skip
                    // pages (previously this scrolled 2x the viewport, skipping a page per keypress).
                    int feedPage = Math.Max(1, viewport.Height - 5);
                    if (k.Key == ConsoleKey.PageUp) feed.ScrollLines(int.MaxValue, feedPage);
                    if (k.Key == ConsoleKey.PageDown) feed.ScrollLines(int.MinValue, feedPage);
                }
                if (rawInput != null)
                {
                    // Raw mode: drain decoded events. Mouse wheel scrolls the feed
                    // (3 lines per notch); keys flow through the normal handler.
                    while (rawInput.TryDequeue(out var ev))
                    {
                        if (ev.Kind == TerminalInputKind.Wheel)
                        {
                            int page = Math.Max(1, viewport.Height - 5);
                            feed.ScrollLines(ev.WheelDelta * 3, page);
                        }
                        else
                        {
                            await HandleKey(ev.Key);
                        }
                        if (!running) break;
                    }
                }
                else
                {
                    try
                    {
                        while (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(intercept: true);
                            await HandleKey(k);
                            if (!running) break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        while (Console.In.Peek() != -1)
                        {
                            var k = Console.ReadKey(intercept: true);
                            await HandleKey(k);
                            if (!running) break;
                        }
                    }
                }
                // Render placeholder + CLI widgets
                var b = new DL.DisplayListBuilder();
                b.PushClip(new DL.ClipPush(0, 0, viewport.Width, viewport.Height));

                var theme = Andy.Cli.Themes.Theme.Current;
                // Opaque themes paint a full-surface background so switching themes
                // recolors the whole screen; transparent themes leave it unpainted so
                // the terminal background (including its transparency) shows through.
                if (!theme.HasTransparentBackground && theme.Background is { } surfaceBg)
                {
                    b.DrawRect(new DL.Rect(0, 0, viewport.Width, viewport.Height, surfaceBg));
                }

                // Draw header with full-width background
                b.DrawRect(new DL.Rect(0, 0, viewport.Width, 1, theme.HeaderBackground));

                // Prepare header components
                var gitInfo = GetGitInfo();

                // Get the human-readable application version for display
                var displayVersion = VersionInfo.ResolveDisplayVersion();
                var version = string.IsNullOrEmpty(displayVersion) ? "" : $" v{displayVersion}";

                var leftSection = $"Andy CLI{version}";
                var rightSection = $"[{gitInfo.branch}@{gitInfo.commit}]";
                var currentPath = Directory.GetCurrentDirectory();

                // Calculate available space for centered path
                int leftLen = leftSection.Length;
                int rightLen = rightSection.Length;
                int padding = 2; // Padding from edges

                // Reserve space: left padding + left section + padding + right section + right padding
                int reservedSpace = padding + leftLen + padding + padding + rightLen + padding;
                int availableForPath = Math.Max(0, viewport.Width - reservedSpace);

                // Truncate path if necessary, showing the end part (most relevant)
                string displayPath = currentPath;
                if (availableForPath > 10 && displayPath.Length > availableForPath)
                {
                    // Truncate from the left, showing the end
                    displayPath = "..." + displayPath.Substring(displayPath.Length - availableForPath + 3);
                }
                else if (availableForPath <= 10)
                {
                    // Not enough space for path
                    displayPath = "";
                }

                // Render left section (Andy CLI with version)
                b.DrawText(new DL.TextRun(padding, 0, leftSection, theme.HeaderTitle, theme.HeaderBackground, DL.CellAttrFlags.Bold));

                // Render centered path (if there's room)
                if (!string.IsNullOrEmpty(displayPath))
                {
                    int pathX = (viewport.Width - displayPath.Length) / 2;
                    // Ensure path doesn't overlap with left section
                    pathX = Math.Max(pathX, padding + leftLen + padding);
                    // Ensure path doesn't overlap with right section
                    if (pathX + displayPath.Length + padding + rightLen + padding <= viewport.Width)
                    {
                        b.DrawText(new DL.TextRun(pathX, 0, displayPath, theme.HeaderPath, theme.HeaderBackground, DL.CellAttrFlags.None));
                    }
                }

                // Render right section (git info)
                int gitX = viewport.Width - rightLen - padding;
                // Ensure git info doesn't overlap with left section
                if (gitX > padding + leftLen + padding)
                {
                    b.DrawText(new DL.TextRun(gitX, 0, rightSection, theme.HeaderGitInfo, theme.HeaderBackground, DL.CellAttrFlags.None));
                }

                // Draw a subtle separator line below the header
                for (int i = 0; i < viewport.Width; i++)
                {
                    b.DrawText(new DL.TextRun(i, 1, "─", theme.Separator, null, DL.CellAttrFlags.None));
                }
                var baseDl = b.Build();
                var wb = new DL.DisplayListBuilder();

                // Calculate heights for bottom UI elements.
                // The context status bar now occupies its own full-width bottom row (the token
                // counter that used to share the hints row is folded into it), so the hints bar
                // gets the full viewport width and reserves nothing on its right.
                int reservedRightWidth = 0;

                // Get the actual height needed for hints bar (may be multi-line)
                int hintsBarHeight = hints.GetRequiredHeight(viewport.Width);

                // Bottom layout (from bottom up):
                // - 1 line: context status bar (context metrics + model info)
                // - hintsBarHeight lines: hints bar
                // - 1 line: status message
                // Toast overlaps with content area when visible
                int bottomReserved = hintsBarHeight + 2; // status bar + hints + message

                // Update context status bar with current metrics
                // Use cumulative token counts from the TokenCounter
                int contextTurnCount = 0;
                int contextMaxTokens = 0;
                string contextModel = string.Empty;
                string contextProvider = string.Empty;
                if (aiService != null)
                {
                    var stats = aiService.GetContextStats();
                    contextTurnCount = stats.TurnCount;
                    contextMaxTokens = stats.MaxContextTokens;
                    contextModel = stats.ModelName;
                    contextProvider = stats.ProviderName;
                }
                contextStatusBar.Update(
                    tokenCounter.TotalInputTokens,
                    tokenCounter.TotalOutputTokens,
                    contextMaxTokens,
                    contextTurnCount);
                contextStatusBar.SetModelInfo(contextModel, contextProvider);

                // Main output area and prompt at bottom
                // Ensure we have enough space to render
                if (viewport.Width > 10 && viewport.Height > 8)
                {
                    // Update inline command help filter based on current prompt text
                    inlineCommandHelp.UpdateFilter(prompt.Text);
                    int helpH = inlineCommandHelp.GetHeight();

                    // Keep the prompt's wrap width in sync with its render width so soft-wrapping,
                    // height measurement and cursor positioning all agree. Render width below is
                    // (viewport.Width - 4); inner text width subtracts the 2 borders and the 3-char
                    // " > " prefix.
                    int promptRenderW = Math.Max(1, viewport.Width - 4);
                    prompt.SetWrapWidth(Math.Max(0, promptRenderW - 2 - 3));

                    int promptH = Math.Min(prompt.GetDesiredHeight(), Math.Max(3, viewport.Height / 2));

                    // Position prompt and help from the bottom up (no gaps)
                    // Layout from bottom: hints(hintsBarHeight) + status(1) + message(1) + help + prompt
                    int promptY = Math.Max(3, viewport.Height - bottomReserved - helpH - promptH);
                    int helpY = promptY + promptH;

                    // Feed fills the space from header to prompt
                    int outputH = Math.Max(1, promptY - 3);

                    // main area: stacked feed with bottom-follow and animation
                    feed.Tick();
                    feed.Render(new L.Rect(2, 3, Math.Max(1, viewport.Width - 4), outputH), baseDl, wb);
                    prompt.Render(new L.Rect(2, promptY, Math.Max(1, viewport.Width - 4), promptH), baseDl, wb);

                    // Render inline command help below the prompt (if visible)
                    if (helpH > 0)
                    {
                        inlineCommandHelp.Render(2, helpY, Math.Max(1, viewport.Width - 4), baseDl, wb);
                    }

                    // Draw scroll mode indicators in left margin
                    if (scrollMode == ScrollMode.Feed)
                    {
                        // Draw a prominent vertical line along the feed area
                        var feedColor = new DL.Rgb24(100, 150, 255); // Blue indicator
                        for (int y = 3; y < 3 + outputH && y < viewport.Height; y++)
                        {
                            wb.DrawText(new DL.TextRun(0, y, "│", feedColor, null, DL.CellAttrFlags.Bold));
                        }

                        // Add scroll position indicator if not at bottom
                        int feedScrollOffset = feed.ScrollLines(0, 0); // Get current offset without changing it
                        if (feedScrollOffset > 0)
                        {
                            // Calculate scroll percentage
                            int totalLines = feed.ScrollLines(0, 0); // This returns current offset, not total
                            string scrollIndicator = $" ▲ {feedScrollOffset} ";
                            int indicatorX = Math.Max(0, viewport.Width - scrollIndicator.Length - 2);
                            int indicatorY = 3;
                            wb.DrawText(new DL.TextRun(indicatorX, indicatorY, scrollIndicator, new DL.Rgb24(100, 150, 255), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.Bold));
                        }
                    }
                    else if (scrollMode == ScrollMode.PromptHistory)
                    {
                        // Draw indicator along prompt area
                        var historyColor = new DL.Rgb24(255, 200, 100); // Orange indicator
                        for (int y = promptY; y < promptY + promptH && y < viewport.Height; y++)
                        {
                            wb.DrawText(new DL.TextRun(0, y, "│", historyColor, null, DL.CellAttrFlags.Bold));
                        }

                        // Draw history counter if navigating history
                        if (historyIndex >= 0 && promptHistory.Count > 0)
                        {
                            string counter = $" {historyIndex + 1}/{promptHistory.Count} ";
                            int counterX = Math.Max(0, viewport.Width - counter.Length - 2);
                            wb.DrawText(new DL.TextRun(counterX, promptY, counter, new DL.Rgb24(255, 200, 100), new DL.Rgb24(40, 40, 50), DL.CellAttrFlags.Bold));
                        }
                    }
                }
                else
                {
                    // Window too small - show minimal message
                    b.DrawText(new DL.TextRun(2, 2, "Window too small", new DL.Rgb24(255, 100, 100), null, DL.CellAttrFlags.Bold));
                    b.DrawText(new DL.TextRun(2, 3, $"Min: {MIN_WIDTH}x{MIN_HEIGHT}", new DL.Rgb24(200, 200, 200), null, DL.CellAttrFlags.None));
                }

                // Render bottom UI elements (context status bar, hints, status message)
                // These are positioned from the bottom up
                int statusMessageY = viewport.Height - hintsBarHeight - 2;
                statusMessage.RenderAt(2, statusMessageY, Math.Max(0, viewport.Width - 4), baseDl, wb);

                toast.Tick(); // Advance toast TTL
                int toastY = viewport.Height - hintsBarHeight - 3;
                toast.RenderAt(2, toastY, baseDl, wb);

                // Render hints into a viewport one row shorter so they sit ABOVE the context
                // status bar (which the bar owns at viewport.Height - 1). KeyHintsBar anchors
                // itself to the bottom of the viewport it is given, so shrinking the height by
                // one lifts it clear of the bar instead of being painted over.
                hints.Render((viewport.Width, viewport.Height - 1), baseDl, wb, reservedRightWidth);

                // Render the context status bar on the bottom row of the viewport
                contextStatusBar.SetLiveStats(aiService?.LiveStats);
                contextStatusBar.Render(viewport, baseDl, wb);

                // Render command palette (if open) into a SEPARATE builder. Overlays
                // must occlude the content behind them, so they keep their own
                // (opaque) background instead of being made transparent like the
                // main surface — otherwise the feed bleeds through the palette.
                var overlayB = new DL.DisplayListBuilder();
                commandPalette.Render(new L.Rect(0, 0, viewport.Width, viewport.Height), baseDl, overlayB);
                permissionsManager.Render(new L.Rect(0, 0, viewport.Width, viewport.Height), baseDl, overlayB);

                var overlay = new DL.DisplayListBuilder();
                hud.ViewportCols = viewport.Width; hud.ViewportRows = viewport.Height;
                hud.Contribute(baseDl, overlay);
                // Combine: main surface is rendered transparent (Append strips
                // backgrounds); overlays are rendered opaque (AppendOpaque keeps them).
                var builder = new DL.DisplayListBuilder();
                foreach (var op in baseDl.Ops) Append(op, builder);
                foreach (var op in wb.Build().Ops) Append(op, builder);
                foreach (var op in overlayB.Build().Ops) AppendOpaque(op, builder);
                foreach (var op in overlay.Build().Ops) AppendOpaque(op, builder);

                // The renderer only repaints cells it draws, so with transparent
                // backgrounds the unmanaged left margin / gap cells can reveal stale
                // "prior output" left in the terminal. Whenever feed content reflows
                // (items added/removed, line count change) OR the user scrolls, force
                // the next frame to be a full clear + repaint (ESC[2J) so that residue
                // is wiped. Scrolling must be included: it shifts content and leaves
                // stale glyphs (e.g. a lone "." or "n") in the margin/gap columns that
                // the diff renderer never overwrites. Manual scrolling changes the
                // offset discretely (per wheel notch / PageUp), so this is one repaint
                // per scroll action, not per frame.
                int reflowSig = HashCode.Combine(feed.ItemCount, feed.RenderedLineCount, (int)scrollMode, feed.ScrollOffset);
                if (reflowSig != lastReflowSig)
                {
                    lastReflowSig = reflowSig;
                    // FrameScheduler has no "reset" hook; a fresh instance has no previous
                    // grid, so its next render emits a full clear + complete repaint.
                    scheduler = new Andy.Tui.Core.FrameScheduler(targetFps: 30);
                    scheduler.SetMetricsSink(hud);
                }
                await scheduler.RenderOnceAsync(builder.Build(), viewport, caps, pty, CancellationToken.None);
                // Position terminal cursor as a block inside the prompt (only when not processing)
                // NOTE: We're using direct Console.Write here instead of going through the TUI library (PTY).
                // This works because cursor positioning happens after frame rendering and doesn't modify
                // the display buffer. However, this creates mixed output paths which is not ideal
                // architecturally. Consider refactoring to route cursor operations through the TUI
                // library's PTY interface or checking if Andy.Tui has built-in cursor support.
                if (!isProcessingMessage && prompt.TryGetTerminalCursor(out int col1, out int row1))
                {
                    if (!cursorStyledShown)
                    {
                        // Set blinking block cursor once and show cursor once (DECSCUSR 1 is blinking block)
                        Console.Write("\u001b[1 q\u001b[?25h");
                        cursorStyledShown = true;
                    }
                    Console.Write($"\u001b[{row1};{col1}H");
                }
                else if (isProcessingMessage)
                {
                    // Hide cursor completely during processing
                    if (cursorStyledShown)
                    {
                        Console.Write("\u001b[?25l"); // Hide cursor
                        cursorStyledShown = false;
                    }
                }

                // Main surface. For a transparent theme, drop any fill/bg a widget
                // hardcoded so the terminal background shows through (e.g. message blocks
                // that bake in a black background). For an opaque theme, keep the
                // backgrounds so switching themes actually recolors the surface.
                static void Append(object op, DL.DisplayListBuilder b)
                {
                    bool transparent = Andy.Cli.Themes.Theme.Current.HasTransparentBackground;
                    switch (op)
                    {
                        case DL.Rect r: b.DrawRect(transparent ? r with { Fill = null } : r); break;
                        case DL.Border br: b.DrawBorder(br); break;
                        case DL.TextRun tr: b.DrawText(transparent ? tr with { Bg = null } : tr); break;
                        case DL.ClipPush cp: b.PushClip(cp); break;
                        case DL.LayerPush lp: b.PushLayer(lp); break;
                        case DL.Pop: b.Pop(); break;
                    }
                }

                // Overlays (command palette, menus): keep their own opaque background so
                // they occlude the content behind them and stay readable.
                static void AppendOpaque(object op, DL.DisplayListBuilder b)
                {
                    switch (op)
                    {
                        case DL.Rect r: b.DrawRect(r); break;
                        case DL.Border br: b.DrawBorder(br); break;
                        case DL.TextRun tr: b.DrawText(tr); break;
                        case DL.ClipPush cp: b.PushClip(cp); break;
                        case DL.LayerPush lp: b.PushLayer(lp); break;
                        case DL.Pop: b.Pop(); break;
                    }
                }
                await Task.Delay(16);
            }
        }
        finally
        {
            // Deterministically stop the instrumentation server (releases the bound
            // port, unsubscribes from the hub, and closes active SSE streams).
            instrumentationServer?.Dispose();

            // Restore terminal settings + disable mouse before leaving the
            // alternate screen, then re-enable wrap/cursor and exit alt screen.
            rawInput?.Dispose();
            Console.Write("\u001b[?7h\u001b[?25h\u001b[?1049l");
        }
    }

    private static void AddReplyToFeed(FeedView feed, string reply, int inputTokens, int outputTokens)
    {
        // Support:
        // - Triple-backtick fenced code blocks with optional language
        // - Malformed single-backtick language blocks (`csharp ... \n`)
        // - Inline single-line JSON objects rendered as json code blocks
        var text = reply.Replace("\r\n", "\n").Replace('\r', '\n');

        // First, extract and render any malformed single-backtick language blocks using regex
        var singleBlockRegex = new System.Text.RegularExpressions.Regex(
            @"(?m)^[ \t]*`([A-Za-z#]+)\s*\r?\n([\s\S]*?)^[ \t]*`\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        while (true)
        {
            var m = singleBlockRegex.Match(text);
            if (!m.Success) break;
            var langToken = m.Groups[1].Value.Trim().ToLowerInvariant();
            var codeBody = m.Groups[2].Value;
            if (!string.IsNullOrEmpty(codeBody)) feed.AddCode(codeBody, langToken);
            // Remove this occurrence and continue
            text = text.Remove(m.Index, m.Length);
        }

        // Render triple-backtick fenced blocks while preserving non-code text
        int i = 0;
        while (i < text.Length)
        {
            int fenceStart = text.IndexOf("```", i, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                var md = text.Substring(i);
                RenderMarkdownWithInlineJson(md, feed);
                break;
            }
            // markdown before code fence
            if (fenceStart > i)
            {
                var md = text.Substring(i, fenceStart - i);
                RenderMarkdownWithInlineJson(md, feed);
            }
            int langEnd2 = text.IndexOf('\n', fenceStart + 3);
            string? lang2 = null;
            if (langEnd2 > fenceStart + 3)
            {
                lang2 = text.Substring(fenceStart + 3, langEnd2 - (fenceStart + 3)).Trim();
                i = langEnd2 + 1;
            }
            else { i = fenceStart + 3; }
            int fenceEnd = text.IndexOf("```", i, StringComparison.Ordinal);
            if (fenceEnd < 0) fenceEnd = text.Length;
            var code2 = text.Substring(i, fenceEnd - i);
            if (!string.IsNullOrEmpty(code2)) feed.AddCode(code2, lang2);
            i = Math.Min(text.Length, fenceEnd + 3);
        }

        // Add static response separator with token information
        feed.AddResponseSeparator(inputTokens, outputTokens);
    }

    private static void RenderMarkdownWithInlineJson(string md, FeedView feed)
    {
        if (string.IsNullOrEmpty(md)) return;
        var lines = md.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("{") && t.EndsWith("}"))
            {
                // Flush any buffered markdown before adding code block
                if (sb.Length > 0)
                {
                    feed.AddMarkdownRich(sb.ToString().TrimEnd('\n'));
                    sb.Clear();
                }
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(t);
                    feed.AddCode(t, "json");
                    continue;
                }
                catch { /* fallthrough to markdown */ }
            }
            sb.AppendLine(line);
        }
        if (sb.Length > 0)
        {
            feed.AddMarkdownRich(sb.ToString().TrimEnd('\n'));
        }
    }

    private static async Task RunAcpServerModeAsync()
    {
        // Configure logging with stderr output (to avoid polluting stdout used for ACP protocol)
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Information;
            })
            .SetMinimumLevel(LogLevel.Information);
        });

        // Configure LLM services
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            // Auto-detect the default provider based on environment variables
            var detectionService = new ProviderDetectionService();
            var detectedProvider = detectionService.DetectDefaultProvider();
            options.DefaultProvider = detectedProvider ?? "cerebras";
        });

        // Add the provider factory
        services.AddSingleton<Andy.Llm.Providers.ILlmProviderFactory, Andy.Llm.Providers.LlmProviderFactory>();

        // Configure the core Andy.Tools service graph (non-interactive: fail-closed /
        // bypass via env). Shared with the interactive and one-shot command paths.
        AppCompositionRoot.AddCoreToolServices(services, null);

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Starting Andy CLI in ACP server mode");

        try
        {
            // Initialize tool registry
            var toolRegistry = AppCompositionRoot.InitializeToolRegistry(serviceProvider);

            logger.LogInformation("Registered {ToolCount} tools", toolRegistry.GetTools().Count());

            // Get required services for the agent
            var providerFactory = serviceProvider.GetRequiredService<Andy.Llm.Providers.ILlmProviderFactory>();

            // Detect current provider
            var detectionService = new ProviderDetectionService();
            var currentProvider = detectionService.DetectDefaultProvider() ?? "cerebras";
            logger.LogInformation("Using LLM provider: {Provider}", currentProvider);

            // Create LLM provider instance
            var llmProvider = providerFactory.CreateProvider(currentProvider);
            if (llmProvider == null)
            {
                throw new InvalidOperationException($"Failed to create LLM provider for: {currentProvider}");
            }

            var toolExecutor = serviceProvider.GetRequiredService<IToolExecutor>();

            // Create the Andy agent provider. Passing the logger factory lets it
            // build a proper typed logger for each engine agent, and the provider
            // is disposed on shutdown so all retained sessions are cleaned up.
            using var agentProvider = new Andy.Cli.ACP.AndyAgentProvider(
                llmProvider,
                toolRegistry,
                toolExecutor,
                loggerFactory.CreateLogger<Andy.Cli.ACP.AndyAgentProvider>(),
                loggerFactory,
                defaultModel: currentProvider);

            logger.LogInformation("Andy agent provider initialized");

            // Derive server metadata from the running assembly so the advertised
            // name/version cannot drift from the packaged CLI build.
            var serverInfo = new Andy.Acp.Core.Protocol.ServerInfo
            {
                Name = Andy.Cli.ACP.AcpServerMetadata.GetName(),
                Version = Andy.Cli.ACP.AcpServerMetadata.GetVersion(),
                Description = "Andy CLI - AI-powered command line assistant"
            };

            // Create and run the ACP server. Filesystem/terminal are agent-to-client
            // requests in conformant ACP, not server-side providers.
            var acpServer = new Andy.Acp.Core.Server.AcpServer(
                agentProvider,
                serverInfo: serverInfo,
                loggerFactory: loggerFactory);

            await acpServer.RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ACP server terminated with error");
            Console.Error.WriteLine($"ACP server error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static async Task HandleCommandLineArgs(string[] args)
    {
        // Initialize services for command-line mode
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            // Disable console logging to avoid UI interference
            // To enable debugging, set environment variable ANDY_DEBUG=true
            if (Environment.GetEnvironmentVariable("ANDY_DEBUG") == "true")
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            }
        });

        // Configure LLM services
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            // Auto-detect the default provider based on environment variables
            var detectionService = new ProviderDetectionService();
            var detectedProvider = detectionService.DetectDefaultProvider();
            options.DefaultProvider = detectedProvider ?? "cerebras"; // Fallback to Cerebras if none detected
        });

        // JSON repair still available if needed elsewhere
        services.AddSingleton<IJsonRepairService, JsonRepairService>();
        // Remove custom parsers/renderers; rely on andy-llm structured outputs
        // services.AddSingleton<StreamingToolCallAccumulator>();
        // services.AddSingleton<IQwenResponseParser, SimpleQwenParser>();
        // services.AddSingleton<IToolCallValidator, ToolCallValidator>();
        // services.AddTransient<Andy.Cli.Parsing.Compiler.LlmResponseCompiler>();
        // services.AddTransient<Andy.Cli.Parsing.Rendering.AstRenderer>();

        // Configure the core Andy.Tools service graph (non-interactive: fail-closed /
        // bypass via env). Shared with the interactive and ACP paths.
        AppCompositionRoot.AddCoreToolServices(services, null);

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tool registry and register tools
        AppCompositionRoot.InitializeToolRegistry(serviceProvider);

        // Route to appropriate command
        var commandName = args[0].ToLowerInvariant();

        // Filter out common flags from the arguments before passing to command
        var filteredArgs = args.Skip(1)
            .Where(arg => !arg.StartsWith("--debug", StringComparison.OrdinalIgnoreCase) &&
                          !arg.StartsWith("--verbose", StringComparison.OrdinalIgnoreCase) &&
                          !arg.StartsWith("--quiet", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var commandArgs = filteredArgs;

        ICommand? command = null;

        switch (commandName)
        {
            case "model":
            case "m":
                command = new ModelCommand(serviceProvider);
                break;
            case "tools":
            case "tool":
            case "t":
                command = new ToolsCommand(serviceProvider);
                break;
            case "permissions":
            case "perms":
            case "perm":
                command = new PermissionsCommand(serviceProvider);
                break;
            case "help":
            case "?":
                Console.WriteLine("Andy CLI - AI Assistant Command Line Interface");
                Console.WriteLine();
                Console.WriteLine("Usage: andy-cli [command] [arguments]");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine("  model, m       - Manage AI models");
                Console.WriteLine("  tools, t       - Manage and list available tools");
                Console.WriteLine("  permissions    - View and modify tool permission rules");
                Console.WriteLine("  help, ?        - Show this help message");
                Console.WriteLine();
                Console.WriteLine("Run without arguments to start the interactive TUI mode.");
                return;
            default:
                Console.WriteLine($"Unknown command: {commandName}");
                Console.WriteLine("Use 'andy-cli help' for usage information.");
                Environment.Exit(1);
                return;
        }

        if (command != null)
        {
            var result = await command.ExecuteAsync(commandArgs);
            Console.WriteLine(result.Message);

            if (!result.Success)
            {
                Environment.Exit(1);
            }
        }
    }

    private static string BuildSystemPrompt(IEnumerable<ToolRegistration> availableTools, string currentModel, string currentProvider)
    {
        // Convert tool registrations to ToolInfo format
        var tools = availableTools.Select(t => new ToolInfo
        {
            Name = t.Metadata.Name,
            Description = t.Metadata.Description,
            Parameters = new List<ToolParameterInfo>() // Could be enhanced later to include actual parameters
        }).ToList();

        // Build custom instructions with model/provider info
        var customInstructions = new StringBuilder();
        customInstructions.AppendLine("Current configuration:");
        customInstructions.AppendLine($"- Model: {currentModel}");
        customInstructions.AppendLine($"- Provider: {currentProvider}");
        customInstructions.AppendLine();
        customInstructions.AppendLine("Provide helpful, accurate responses. When using tools, explain what you're doing.");

        return SystemPrompts.GetPromptWithTools(tools, customInstructions.ToString());
    }
}

file sealed class LocalStdoutPty : Andy.Tui.Backend.Terminal.IPtyIo
{
    public Task WriteAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        Console.Write(System.Text.Encoding.UTF8.GetString(frameBytes.Span));
        return Task.CompletedTask;
    }
}