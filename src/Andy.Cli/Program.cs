using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tui.Backend.Terminal;
using Andy.Cli.Widgets;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Cli.Tools;
using Andy.Llm;
using Andy.Llm.Extensions;
using Andy.Model.Model;
using Andy.Model.Orchestration;
using Andy.Tools;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli;

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

    static async Task Main(string[] args)
    {
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

        // Check if we have command-line arguments for non-TUI commands
        if (args.Length > 0 && !args[0].StartsWith("-"))
        {
            await HandleCommandLineArgs(args);
            return;
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

        // Set console background to black after entering alternate screen
        Console.BackgroundColor = ConsoleColor.Black;
        Console.Clear();

        try
        {
            bool running = true;
            var hints = new KeyHintsBar();
            hints.SetHints(new[] { ("Ctrl+P", "Commands"), ("F2", "Toggle HUD"), ("ESC", "Quit") });
            var toast = new Toast(); // Don't show initial toast as it interferes with prompt
            var tokenCounter = new TokenCounter();
            var statusMessage = new StatusMessage();
            var status = new StatusLine(); status.Set("Idle", spinner: false);
            var prompt = new PromptLine();
            prompt.SetBorder(true);
            prompt.SetShowCaret(true);
            prompt.SetFocused(true);
            var feed = new FeedView();
            feed.SetFocused(false);
            feed.SetAnimationSpeed(8); // faster scroll-in
            feed.AddMarkdownRich("**Ready to assist!** What can I help you learn or explore today?");

            // Initialize Andy.Llm and Andy.Tools services
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
            }); // Conditional logging based on environment variable

            // Configure LLM services
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                // Auto-detect the default provider based on environment variables
                var detectionService = new ProviderDetectionService();
                var detectedProvider = detectionService.DetectDefaultProvider();
                options.DefaultProvider = detectedProvider ?? "cerebras"; // Fallback to Cerebras if none detected
            });

            // Add the provider factory if not already added
            services.AddSingleton<Andy.Llm.Providers.ILlmProviderFactory, Andy.Llm.Providers.LlmProviderFactory>();

            // JSON repair still available if needed elsewhere
            services.AddSingleton<IJsonRepairService, JsonRepairService>();
            // Remove custom parsers/renderers; rely on andy-llm structured outputs
            // services.AddSingleton<StreamingToolCallAccumulator>();
            // services.AddSingleton<IQwenResponseParser, SimpleQwenParser>();
            // services.AddSingleton<IToolCallValidator, ToolCallValidator>();
            // services.AddTransient<Andy.Cli.Parsing.Compiler.LlmResponseCompiler>();
            // services.AddTransient<Andy.Cli.Parsing.Rendering.AstRenderer>();

            // Configure Tool services - manually register to avoid HostedService requirement
            // Core services from AddAndyTools
            services.AddSingleton<Andy.Tools.Validation.IToolValidator, Andy.Tools.Validation.ToolValidator>();
            services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
            services.AddSingleton<Andy.Tools.Discovery.IToolDiscovery, Andy.Tools.Discovery.ToolDiscoveryService>();
            services.AddSingleton<Andy.Tools.Execution.ISecurityManager, Andy.Tools.Execution.SecurityManager>();
            services.AddSingleton<Andy.Tools.Execution.IResourceMonitor, Andy.Tools.Execution.ResourceMonitor>();
            services.AddSingleton<Andy.Tools.Core.OutputLimiting.IToolOutputLimiter, Andy.Tools.Core.OutputLimiting.ToolOutputLimiter>();
            services.AddSingleton<IToolExecutor, ToolExecutor>();
            services.AddSingleton<Andy.Tools.Core.IPermissionProfileService, Andy.Tools.Core.PermissionProfileService>();
            services.AddSingleton<Andy.Tools.Framework.IToolLifecycleManager, Andy.Tools.Framework.ToolLifecycleManager>();

            // Framework options
            services.AddSingleton(new Andy.Tools.Framework.ToolFrameworkOptions
            {
                RegisterBuiltInTools = false, // We'll register them separately
                EnableObservability = false,
                AutoDiscoverTools = false
            });

            // Register built-in tools
            services.AddBuiltInTools();

            // Register custom tools
            services.AddSingleton(new ToolRegistrationInfo
            {
                ToolType = typeof(Andy.Cli.Tools.CreateDirectoryTool),
                Configuration = new Dictionary<string, object?>()
            });
            services.AddSingleton(new ToolRegistrationInfo
            {
                ToolType = typeof(Andy.Cli.Tools.BashCommandTool),
                Configuration = new Dictionary<string, object?>()
            });
            services.AddSingleton(new ToolRegistrationInfo
            {
                ToolType = typeof(Andy.Cli.Tools.CodeIndexTool),
                Configuration = new Dictionary<string, object?>()
            });

            // Register code indexing service
            services.AddSingleton<CodeIndexingService>();
            services.AddHostedService<CodeIndexingService>(provider => provider.GetRequiredService<CodeIndexingService>());

            var serviceProvider = services.BuildServiceProvider();

            // Initialize tool registry and register tools
            var toolRegistry = serviceProvider.GetRequiredService<IToolRegistry>();
            var toolRegistrations = serviceProvider.GetServices<ToolRegistrationInfo>();
            foreach (var registration in toolRegistrations)
            {
                toolRegistry.RegisterTool(registration.ToolType, registration.Configuration);
            }

            // Initialize commands
            var modelCommand = new ModelCommand(serviceProvider);
            var toolsCommand = new ToolsCommand(serviceProvider);
            var commandPalette = new CommandPalette();

            Andy.Model.Llm.ILlmProvider? llmProvider = null;
            AssistantService? aiService = null;

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
                var logger = serviceProvider.GetService<ILogger<AssistantService>>();
                aiService = new AssistantService(
                    llmProvider,
                    toolRegistry,
                    toolExecutor,
                    feed,
                    systemPrompt,
                    logger,
                    currentModel,
                    currentProvider);

                // Get actual model and provider information from ModelCommand
                // Set initial model info
                aiService.UpdateModelInfo(currentModel, currentProvider);
                var providerUrl = GetProviderUrl(currentProvider);

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
                    Action = args =>
                    {
                        running = false;
                    }
                },
                new CommandPalette.CommandItem
                {
                    Name = "List Models",
                    Description = "Show available AI models",
                    Category = "Model",
                    Aliases = new[] { "models", "list" },
                    Action = async args =>
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
                    Action = async args =>
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
                                    var logger = serviceProvider.GetService<ILogger<AssistantService>>();
                                    aiService = new AssistantService(
                                        llmProvider,
                                        toolRegistry,
                                        toolExecutor,
                                        feed,
                                        systemPrompt,
                                        logger,
                                        newModel,
                                        newProvider);
                                    
                                    // Set model info for response interpretation
                                    aiService.UpdateModelInfo(
                                        modelCommand.GetCurrentModel(),
                                        modelCommand.GetCurrentProvider()
                                    );
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
                    Action = async args =>
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
                    Action = async args =>
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
                    Action = async args =>
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
                    Action = async args =>
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
                        // Recreate AssistantService to clear context
                        if (aiService != null)
                        {
                            aiService.ClearContext();
                        }
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
                    Name = "Help",
                    Description = "Show help information",
                    Category = "General",
                    Aliases = new[] { "?", "help" },
                    Action = args =>
                    {
                        feed.AddMarkdownRich("# Andy CLI Help\n\n" +
                            "## Keyboard Shortcuts:\n" +
                            "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
                            "- **F2**: Toggle HUD (performance overlay)\n" +
                            "- **ESC**: Quit application\n" +
                            "- **↑/↓**: Scroll chat history\n" +
                            "- **Page Up/Down**: Fast scroll\n\n" +
                            "## Commands:\n" +
                            "- **/model list**: Show available models\n" +
                            "- **/model switch <provider>**: Change provider\n" +
                            "- **/model info**: Show current model details\n" +
                            "- **/model test [prompt]**: Test current model\n\n" +
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
                // Input (prefer KeyAvailable; fallback to Console.In.Peek in non-interactive contexts)
                async Task HandleKey(ConsoleKeyInfo k)
                {
                    if (k.Key == ConsoleKey.Escape)
                    {
                        // If command palette is open, close it first
                        if (commandPalette.IsOpen)
                        {
                            commandPalette.Close();
                            return;
                        }

                        // Confirmation dialog with buttons
                        bool confirmExit = false;
                        bool dialogOpen = true;
                        bool yesSelected = false; // Default to No

                        while (dialogOpen)
                        {
                            var confirmB = new DL.DisplayListBuilder();
                            confirmB.PushClip(new DL.ClipPush(0, 0, viewport.Width, viewport.Height));

                            // Semi-transparent backdrop
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
                            ConsoleKeyInfo k2 = Console.ReadKey(true);
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

                        if (confirmExit) { running = false; }
                        return;
                    }
                    if (k.Key == ConsoleKey.F2) { hud.Enabled = !hud.Enabled; return; }

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
                                commandPalette.ExecuteSelected();
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
                                commandPalette.ExecuteSelected();
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

                    // Avoid mapping regular alphanumeric keys to actions
                    var submitted = prompt.OnKey(k);
                    if (submitted is string cmd && !string.IsNullOrWhiteSpace(cmd))
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
                                                var logger = serviceProvider.GetService<ILogger<AssistantService>>();
                                                aiService = new AssistantService(
                                                    llmProvider,
                                                    toolRegistry,
                                                    toolExecutor,
                                                    feed,
                                                    systemPrompt,
                                                    logger,
                                                    newModel,
                                                    newProvider);

                                                // Set model info for response interpretation
                                                aiService.UpdateModelInfo(
                                                    modelCommand.GetCurrentModel(),
                                                    modelCommand.GetCurrentProvider()
                                                );
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
                                else if (commandName == "help" || commandName == "?")
                                {
                                    feed.AddUserMessage(cmd);
                                    feed.AddMarkdownRich("# Andy CLI Help\n\n" +
                                        "## Keyboard Shortcuts:\n" +
                                        "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
                                        "- **F2**: Toggle HUD (performance overlay)\n" +
                                        "- **ESC**: Quit application\n" +
                                        "- **↑/↓**: Scroll chat history\n" +
                                        "- **Page Up/Down**: Fast scroll\n\n" +
                                        "## Commands:\n" +
                                        "### Model Commands:\n" +
                                        "- **/model list**: Show available models\n" +
                                        "- **/model switch <provider>**: Change provider\n" +
                                        "- **/model info**: Show current model details\n" +
                                        "- **/model test [prompt]**: Test current model\n\n" +
                                        "### Tool Commands:\n" +
                                        "- **/tools list [category]**: List available tools\n" +
                                        "- **/tools info <tool_name>**: Show tool details\n" +
                                        "- **/tools execute <tool_name> [params]**: Run a tool\n\n" +
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
                                else if (commandName == "clear")
                                {
                                    // Recreate AssistantService to clear context
                                    if (aiService != null)
                                    {
                                        aiService.ClearContext();
                                    }
                                    feed.Clear();
                                    feed.AddMarkdownRich("**Chat cleared!** Ready for a fresh conversation.");
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

                        // Regular chat message
                        feed.AddUserMessage(cmd);
                        if (aiService != null)
                        {
                            try
                            {
                                statusMessage.SetMessage("Thinking", animated: true);

                                // Process message with tool support (streaming disabled for stability)
                                var response = await aiService.ProcessMessageAsync(cmd, enableStreaming: false);

                                // Get context stats for token counting
                                var stats = aiService.GetContextStats();
                                tokenCounter.AddTokens(stats.EstimatedTokens / 2, stats.EstimatedTokens / 2);

                                statusMessage.SetMessage("Ready for next question", animated: false);
                            }
                            catch (Exception ex)
                            {
                                feed.AddMarkdownRich(ConsoleColors.ErrorPrefix(ex.Message));
                                statusMessage.SetMessage("Error occurred", animated: false);
                            }
                        }
                        else if (llmProvider is not null)
                        {
                            // Fallback to non-tool-aware conversation
                            try
                            {
                                statusMessage.SetMessage("Thinking", animated: true);

                                // For fallback non-tool-aware conversation, create a basic assistant
                                // without tools and run a single turn
                                var fallbackConversation = new Conversation
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    CreatedAt = DateTime.UtcNow
                                };
                                var emptyToolRegistry = new Andy.Model.Tooling.ToolRegistry();
                                var fallbackAssistant = new Assistant(fallbackConversation, emptyToolRegistry, llmProvider);
                                
                                // Get response using the fallback assistant
                                var response = await fallbackAssistant.RunTurnAsync(cmd, CancellationToken.None);

                                // Estimate token usage since Message doesn't provide it
                                int inputTokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 2;
                                int outputTokens = (response.Content ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 2;

                                tokenCounter.AddTokens(inputTokens, outputTokens);

                                AddReplyToFeed(feed, response.Content ?? string.Empty, inputTokens, outputTokens);

                                // Note: Without persistent conversation, each request is independent

                                statusMessage.SetMessage("Ready for next question", animated: false);
                            }
                            catch (Exception ex)
                            {
                                feed.AddMarkdownRich(ConsoleColors.ErrorPrefix(ex.Message));
                                statusMessage.SetMessage("Error occurred", animated: false);
                            }
                        }
                        else
                        {
                            feed.AddMarkdownRich("ok");
                            statusMessage.SetMessage("No AI model connected", animated: false);
                        }
                        return;
                    }
                    if (k.Key == ConsoleKey.UpArrow) feed.ScrollLines(+2, Math.Max(1, viewport.Height - 5));
                    if (k.Key == ConsoleKey.DownArrow) feed.ScrollLines(-2, Math.Max(1, viewport.Height - 5));
                    if (k.Key == ConsoleKey.Tab && (k.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        // Toggle focus between prompt and feed
                        bool promptIsFocusedNow = true; // we set prompt initially focused
                        promptIsFocusedNow = !promptIsFocusedNow;
                        prompt.SetFocused(promptIsFocusedNow);
                        feed.SetFocused(!promptIsFocusedNow);
                        return;
                    }
                    if (k.Key == ConsoleKey.PageUp) feed.ScrollLines(+2 * Math.Max(1, viewport.Height - 5), Math.Max(1, viewport.Height - 5));
                    if (k.Key == ConsoleKey.PageDown) feed.ScrollLines(-2 * Math.Max(1, viewport.Height - 5), Math.Max(1, viewport.Height - 5));
                }
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
                // Render placeholder + CLI widgets
                var b = new DL.DisplayListBuilder();
                b.PushClip(new DL.ClipPush(0, 0, viewport.Width, viewport.Height));
                b.DrawRect(new DL.Rect(0, 0, viewport.Width, viewport.Height, new DL.Rgb24(0, 0, 0)));
                var gitInfo = GetGitInfo();
                var headerText = $"Andy CLI [{gitInfo.branch}@{gitInfo.commit}]";
                b.DrawText(new DL.TextRun(2, 1, headerText, new DL.Rgb24(200, 200, 50), null, DL.CellAttrFlags.Bold));
                var baseDl = b.Build();
                var wb = new DL.DisplayListBuilder();
                hints.Render(viewport, baseDl, wb);
                toast.Tick(); // Advance toast TTL
                toast.RenderAt(2, viewport.Height - 4, baseDl, wb);

                // Render token counter on same line as hints
                int tokenCounterWidth = tokenCounter.GetWidth();
                int tokenCounterX = viewport.Width - tokenCounterWidth - 2;
                if (tokenCounterX > 0)
                {
                    tokenCounter.RenderAt(tokenCounterX, viewport.Height - 1, baseDl, wb);
                }

                // Render status message above "Idle" line
                statusMessage.RenderAt(2, viewport.Height - 3, Math.Max(0, viewport.Width - 4), baseDl, wb);

                status.Tick(); status.Render(viewport, baseDl, wb);
                // Main output area and prompt at bottom
                // Ensure we have enough space to render
                if (viewport.Width > 10 && viewport.Height > 8)
                {
                    int promptH = Math.Clamp(prompt.GetLineCount(), 1, Math.Max(1, viewport.Height / 2));
                    int outputH = Math.Max(1, viewport.Height - 5 - 2);
                    // allocate space for variable-height prompt
                    outputH = Math.Max(1, viewport.Height - 5 - (promptH + 1));
                    // main area: stacked feed with bottom-follow and animation
                    feed.Tick();
                    feed.Render(new L.Rect(2, 3, Math.Max(1, viewport.Width - 4), outputH), baseDl, wb);
                    prompt.Render(new L.Rect(2, 3 + outputH + 1, Math.Max(1, viewport.Width - 4), promptH), baseDl, wb);
                }
                else
                {
                    // Window too small - show minimal message
                    b.DrawText(new DL.TextRun(2, 2, "Window too small", new DL.Rgb24(255, 100, 100), null, DL.CellAttrFlags.Bold));
                    b.DrawText(new DL.TextRun(2, 3, $"Min: {MIN_WIDTH}x{MIN_HEIGHT}", new DL.Rgb24(200, 200, 200), null, DL.CellAttrFlags.None));
                }

                // Render command palette (if open)
                commandPalette.Render(new L.Rect(0, 0, viewport.Width, viewport.Height), baseDl, wb);

                var overlay = new DL.DisplayListBuilder();
                hud.ViewportCols = viewport.Width; hud.ViewportRows = viewport.Height;
                hud.Contribute(baseDl, overlay);
                // Simple combine logic
                var builder = new DL.DisplayListBuilder();
                foreach (var op in baseDl.Ops) Append(op, builder);
                foreach (var op in wb.Build().Ops) Append(op, builder);
                foreach (var op in overlay.Build().Ops) Append(op, builder);
                await scheduler.RenderOnceAsync(builder.Build(), viewport, caps, pty, CancellationToken.None);
                // Position terminal cursor as a thin bar inside the prompt
                if (prompt.TryGetTerminalCursor(out int col1, out int row1))
                {
                    if (!cursorStyledShown)
                    {
                        // Set steady bar cursor once and show cursor once; also disable terminal blink (DECSCUSR 6 is steady bar)
                        Console.Write("\u001b[6 q\u001b[?25h");
                        cursorStyledShown = true;
                    }
                    Console.Write($"\u001b[{row1};{col1}H");
                }

                static void Append(object op, DL.DisplayListBuilder b)
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

    private static string GetProviderUrl(string provider)
    {
        return provider switch
        {
            "cerebras" => "https://api.cerebras.ai",
            "openai" => Environment.GetEnvironmentVariable("OPENAI_API_BASE") ?? "https://api.openai.com",
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com",
            "ollama" => Environment.GetEnvironmentVariable("OLLAMA_API_BASE") ?? "http://localhost:11434",
            _ => "unknown"
        };
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

        // Configure Tool services - manually register to avoid HostedService requirement
        // Core services from AddAndyTools
        services.AddSingleton<Andy.Tools.Validation.IToolValidator, Andy.Tools.Validation.ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<Andy.Tools.Discovery.IToolDiscovery, Andy.Tools.Discovery.ToolDiscoveryService>();
        services.AddSingleton<Andy.Tools.Execution.ISecurityManager, Andy.Tools.Execution.SecurityManager>();
        services.AddSingleton<Andy.Tools.Execution.IResourceMonitor, Andy.Tools.Execution.ResourceMonitor>();
        services.AddSingleton<Andy.Tools.Core.OutputLimiting.IToolOutputLimiter, Andy.Tools.Core.OutputLimiting.ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<Andy.Tools.Core.IPermissionProfileService, Andy.Tools.Core.PermissionProfileService>();
        services.AddSingleton<Andy.Tools.Framework.IToolLifecycleManager, Andy.Tools.Framework.ToolLifecycleManager>();

        // Framework options
        services.AddSingleton(new Andy.Tools.Framework.ToolFrameworkOptions
        {
            RegisterBuiltInTools = false, // We'll register them separately
            EnableObservability = false,
            AutoDiscoverTools = false
        });

        // Register built-in tools
        services.AddBuiltInTools();

        // Register custom tools
        services.AddSingleton(new ToolRegistrationInfo
        {
            ToolType = typeof(Andy.Cli.Tools.CreateDirectoryTool),
            Configuration = new Dictionary<string, object?>()
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tool registry and register tools
        var toolRegistry = serviceProvider.GetRequiredService<IToolRegistry>();
        var toolRegistrations = serviceProvider.GetServices<ToolRegistrationInfo>();
        foreach (var registration in toolRegistrations)
        {
            toolRegistry.RegisterTool(registration.ToolType, registration.Configuration);
        }

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
            case "help":
            case "?":
                Console.WriteLine("Andy CLI - AI Assistant Command Line Interface");
                Console.WriteLine();
                Console.WriteLine("Usage: andy-cli [command] [arguments]");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine("  model, m       - Manage AI models");
                Console.WriteLine("  tools, t       - Manage and list available tools");
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
        var prompt = new StringBuilder();
        prompt.AppendLine("You are an AI assistant with access to the following tools:");
        prompt.AppendLine();

        foreach (var tool in availableTools)
        {
            prompt.AppendLine($"- {tool.Metadata.Name}: {tool.Metadata.Description}");
        }

        prompt.AppendLine();
        prompt.AppendLine($"Current configuration:");
        prompt.AppendLine($"- Model: {currentModel}");
        prompt.AppendLine($"- Provider: {currentProvider}");
        prompt.AppendLine();
        prompt.AppendLine("Provide helpful, accurate responses. When using tools, explain what you're doing.");

        return prompt.ToString();
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