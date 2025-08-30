using System;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tui.Backend.Terminal;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Llm.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli;

class Program
{
    static async Task Main()
    {
        var caps = Andy.Tui.Backend.Terminal.CapabilityDetector.DetectFromEnvironment();
        var viewport = (Width: Console.WindowWidth, Height: Console.WindowHeight);
        var scheduler = new Andy.Tui.Core.FrameScheduler(targetFps: 30);
        var hud = new Andy.Tui.Observability.HudOverlay { Enabled = false };
        scheduler.SetMetricsSink(hud);
        var pty = new LocalStdoutPty();
        Console.Write("\u001b[?1049h\u001b[?25l\u001b[?7l");
        try
        {
            bool running = true;
            var hints = new KeyHintsBar();
            hints.SetHints(new[]{("F2","Toggle HUD"),("ESC","Quit")});
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
            feed.AddMarkdownRich("✨ **Ready to assist!** What can I help you learn or explore today?");
            
            // Initialize Andy.Llm services
            var services = new ServiceCollection();
            services.AddLogging(); // Basic logging without console provider
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = "cerebras"; // Use Cerebras as default
            });
            var serviceProvider = services.BuildServiceProvider();
            
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
                feed.AddMarkdown($"[error] {ex.Message}");
                feed.AddMarkdownRich("[info] Set CEREBRAS_API_KEY to enable AI responses");
            }
            bool cursorStyledShown = false;
            while (running)
            {
                // Input (prefer KeyAvailable; fallback to Console.In.Peek in non-interactive contexts)
                async Task HandleKey(ConsoleKeyInfo k)
                {
                    if (k.Key == ConsoleKey.Escape)
                    {
                        // Confirmation box
                        // Compose one frame with dialog using a fresh base
                        var confirmB = new DL.DisplayListBuilder();
                        confirmB.PushClip(new DL.ClipPush(0,0,viewport.Width, viewport.Height));
                        confirmB.DrawRect(new DL.Rect(0,0,viewport.Width, viewport.Height, new DL.Rgb24(0,0,0)));                        
                        int bw = Math.Min(40, viewport.Width - 4);
                        int bh = 5;
                        int bx = (viewport.Width - bw)/2; int by = (viewport.Height - bh)/2;
                        confirmB.PushClip(new DL.ClipPush(bx, by, bw, bh));
                        confirmB.DrawRect(new DL.Rect(bx, by, bw, bh, new DL.Rgb24(20,20,20)));
                        confirmB.DrawBorder(new DL.Border(bx, by, bw, bh, "single", new DL.Rgb24(200,200,80)));
                        confirmB.DrawText(new DL.TextRun(bx+2, by+2, "Exit? (Y/N)", new DL.Rgb24(220,220,220), new DL.Rgb24(20,20,20), DL.CellAttrFlags.Bold));
                        confirmB.Pop();
                        await scheduler.RenderOnceAsync(confirmB.Build(), viewport, caps, pty, CancellationToken.None);
                        // Wait for Y/N
                        ConsoleKeyInfo k2 = Console.ReadKey(true);
                        if (k2.Key == ConsoleKey.Y) { running = false; return; }
                        else { return; }
                    }
                    if (k.Key == ConsoleKey.F2) { hud.Enabled = !hud.Enabled; return; }
                    // Avoid mapping regular alphanumeric keys to actions
                    var submitted = prompt.OnKey(k);
                    if (submitted is string cmd && !string.IsNullOrWhiteSpace(cmd))
                    {
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
                                feed.AddMarkdownRich("[error] " + ex.Message); 
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
                b.DrawText(new DL.TextRun(2,1, "Andy CLI — ESC:Quit  F2:HUD", new DL.Rgb24(200,200,50), null, DL.CellAttrFlags.Bold));
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
                int promptH = Math.Clamp(prompt.GetLineCount(), 1, Math.Max(1, viewport.Height/2));
                int outputH = Math.Max(1, viewport.Height - 5 - 2);
                // allocate space for variable-height prompt
                outputH = Math.Max(1, viewport.Height - 5 - (promptH + 1));
                // main area: stacked feed with bottom-follow and animation
                feed.Tick();
                feed.Render(new L.Rect(2, 3, Math.Max(0, viewport.Width - 4), outputH), baseDl, wb);
                prompt.Render(new L.Rect(2, 3 + outputH + 1, Math.Max(0, viewport.Width - 4), promptH), baseDl, wb);
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
}

file sealed class LocalStdoutPty : Andy.Tui.Backend.Terminal.IPtyIo
{
    public Task WriteAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        Console.Write(System.Text.Encoding.UTF8.GetString(frameBytes.Span));
        return Task.CompletedTask;
    }
}