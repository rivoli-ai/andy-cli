using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tui.Backend.Terminal;
using Andy.Cli.Widgets;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Llm.Extensions;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Framework;
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
    
    static async Task Main(string[] args)
    {
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
            hints.SetHints(new[]{("Ctrl+P","Commands"),("F2","Toggle HUD"),("ESC","Quit")});
            var toast = new Toast(); toast.Show("What would you like to explore today?", 120);
            var tokenCounter = new TokenCounter();
            var statusMessage = new StatusMessage();
            var status = new StatusLine(); status.Set("Idle", spinner:false);
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
            services.AddLogging(); // Basic logging without console provider
            
            // Configure LLM services
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = "cerebras"; // Use Cerebras as default
            });
            
            // Configure Tool services
            services.AddSingleton<IToolRegistry, ToolRegistry>();
            services.AddSingleton<IToolExecutor, ToolExecutor>();
            services.AddSingleton<ISecurityManager, SecurityManager>();
            services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
            
            // Register built-in tools
            services.AddBuiltInTools();
            
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
            
            LlmClient? llmClient = null;
            var conversation = new ConversationContext
            {
                SystemInstruction = "You are a helpful AI assistant. Keep your responses concise and helpful.",
                MaxContextMessages = 20
            };
            
            try
            {
                llmClient = serviceProvider.GetRequiredService<LlmClient>();
                feed.AddMarkdownRich("[model] Andy.Llm with Cerebras provider");
            }
            catch (Exception ex)
            {
                feed.AddMarkdown(ConsoleColors.ErrorPrefix(ex.Message));
                feed.AddMarkdownRich(ConsoleColors.NotePrefix("Set CEREBRAS_API_KEY to enable AI responses"));
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
                                // Update the LLM client and reset conversation context
                                llmClient = modelCommand.GetCurrentClient();
                                conversation.Clear();
                                conversation.SystemInstruction = "You are a helpful AI assistant. Keep your responses concise and helpful.";
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
                        conversation.Clear();
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
                            confirmB.PushClip(new DL.ClipPush(0,0,viewport.Width, viewport.Height));
                            
                            // Semi-transparent backdrop
                            confirmB.DrawRect(new DL.Rect(0,0,viewport.Width, viewport.Height, new DL.Rgb24(0,0,0)));
                            
                            // Dialog box
                            int bw = Math.Min(44, viewport.Width - 4);
                            int bh = 7;
                            int bx = (viewport.Width - bw)/2; 
                            int by = (viewport.Height - bh)/2;
                            
                            // Draw dialog background and border
                            confirmB.PushClip(new DL.ClipPush(bx, by, bw, bh));
                            confirmB.DrawRect(new DL.Rect(bx, by, bw, bh, new DL.Rgb24(30,30,40)));
                            confirmB.DrawBorder(new DL.Border(bx, by, bw, bh, "double", new DL.Rgb24(200,200,80)));
                            
                            // Title
                            string title = "Exit Application?";
                            int titleX = bx + (bw - title.Length) / 2;
                            confirmB.DrawText(new DL.TextRun(titleX, by+2, title, new DL.Rgb24(255,255,255), new DL.Rgb24(30,30,40), DL.CellAttrFlags.Bold));
                            
                            // Buttons
                            int buttonY = by + 4;
                            int buttonSpacing = 12;
                            int noButtonX = bx + (bw/2) - buttonSpacing;
                            int yesButtonX = bx + (bw/2) + 3;
                            
                            // No button (default)
                            var noBg = !yesSelected ? new DL.Rgb24(60,120,60) : new DL.Rgb24(40,40,50);
                            var noFg = !yesSelected ? new DL.Rgb24(255,255,255) : new DL.Rgb24(180,180,180);
                            confirmB.DrawRect(new DL.Rect(noButtonX, buttonY, 8, 1, noBg));
                            confirmB.DrawText(new DL.TextRun(noButtonX+1, buttonY, !yesSelected ? "[ No ]" : "  No  ", noFg, noBg, !yesSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                            
                            // Yes button
                            var yesBg = yesSelected ? new DL.Rgb24(120,60,60) : new DL.Rgb24(40,40,50);
                            var yesFg = yesSelected ? new DL.Rgb24(255,255,255) : new DL.Rgb24(180,180,180);
                            confirmB.DrawRect(new DL.Rect(yesButtonX, buttonY, 8, 1, yesBg));
                            confirmB.DrawText(new DL.TextRun(yesButtonX+1, buttonY, yesSelected ? "[ Yes ]" : "  Yes  ", yesFg, yesBg, yesSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                            
                            // Hints
                            string hints = "← → Navigate  Enter Select  Esc Cancel";
                            int hintsX = bx + (bw - hints.Length) / 2;
                            confirmB.DrawText(new DL.TextRun(hintsX, by+bh-1, hints, new DL.Rgb24(120,120,150), new DL.Rgb24(30,30,40), DL.CellAttrFlags.None));
                            
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
                                            // Update the LLM client and reset conversation context
                                            llmClient = modelCommand.GetCurrentClient();
                                            conversation.Clear();
                                            conversation.SystemInstruction = "You are a helpful AI assistant. Keep your responses concise and helpful.";
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
                                    conversation.Clear();
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
                        if (llmClient is not null)
                        {
                            try
                            {
                                statusMessage.SetMessage("Thinking", animated: true);
                                
                                // Add user message to conversation context
                                conversation.AddUserMessage(cmd);
                                
                                // Create request with conversation context
                                var request = conversation.CreateRequest();
                                
                                // Get response from Andy.Llm
                                var response = await llmClient.CompleteAsync(request);
                                
                                // Extract token usage if available
                                int inputTokens = 0;
                                int outputTokens = 0;
                                if (response.TokensUsed.HasValue)
                                {
                                    // For now, estimate token distribution (Andy.Llm may provide more detailed info in future)
                                    inputTokens = (int)(response.TokensUsed.Value * 0.3); // Rough estimate
                                    outputTokens = (int)(response.TokensUsed.Value * 0.7);
                                }
                                else
                                {
                                    // Fallback estimation
                                    inputTokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 2;
                                    outputTokens = (response.Content ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 2;
                                }
                                
                                tokenCounter.AddTokens(inputTokens, outputTokens);
                                
                                AddReplyToFeed(feed, response.Content ?? string.Empty, inputTokens, outputTokens);
                                
                                // Add assistant response to conversation context
                                conversation.AddAssistantMessage(response.Content ?? string.Empty);
                                
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
                b.PushClip(new DL.ClipPush(0,0,viewport.Width, viewport.Height));
                b.DrawRect(new DL.Rect(0,0,viewport.Width, viewport.Height, new DL.Rgb24(0,0,0)));
                b.DrawText(new DL.TextRun(2,1, "Andy CLI — Ctrl+P:Commands  ESC:Quit  F2:HUD", new DL.Rgb24(200,200,50), null, DL.CellAttrFlags.Bold));
                var baseDl = b.Build();
                var wb = new DL.DisplayListBuilder();
                hints.Render(viewport, baseDl, wb);
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
                    int promptH = Math.Clamp(prompt.GetLineCount(), 1, Math.Max(1, viewport.Height/2));
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
        // Split by fenced code blocks. Support optional language after ```
        var text = reply.Replace("\r\n","\n").Replace('\r','\n');
        int i = 0;
        while (i < text.Length)
        {
            int fenceStart = text.IndexOf("```", i, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                var md = text.Substring(i);
                if (!string.IsNullOrWhiteSpace(md)) feed.AddMarkdownRich(md);
                break;
            }
            // markdown before code fence
            if (fenceStart > i)
            {
                var md = text.Substring(i, fenceStart - i);
                if (!string.IsNullOrWhiteSpace(md)) feed.AddMarkdownRich(md);
            }
            int langEnd = text.IndexOf('\n', fenceStart + 3);
            string? lang = null;
            if (langEnd > fenceStart + 3)
            {
                lang = text.Substring(fenceStart + 3, langEnd - (fenceStart + 3)).Trim();
                i = langEnd + 1;
            }
            else { i = fenceStart + 3; }
            int fenceEnd = text.IndexOf("```", i, StringComparison.Ordinal);
            if (fenceEnd < 0) fenceEnd = text.Length;
            var code = text.Substring(i, fenceEnd - i);
            if (!string.IsNullOrEmpty(code)) feed.AddCode(code, lang);
            i = Math.Min(text.Length, fenceEnd + 3);
        }
        
        // Add static response separator with token information
        feed.AddResponseSeparator(inputTokens, outputTokens);
    }

    private static async Task HandleCommandLineArgs(string[] args)
    {
        // Initialize services for command-line mode
        var services = new ServiceCollection();
        services.AddLogging();
        
        // Configure LLM services
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = "cerebras";
        });
        
        // Configure Tool services
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
        
        // Register built-in tools
        services.AddBuiltInTools();
        
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
        var commandArgs = args.Skip(1).ToArray();
        
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
}

file sealed class LocalStdoutPty : Andy.Tui.Backend.Terminal.IPtyIo
{
    public Task WriteAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        Console.Write(System.Text.Encoding.UTF8.GetString(frameBytes.Span));
        return Task.CompletedTask;
    }
}