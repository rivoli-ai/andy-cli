// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Instrumentation;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Integration
{
    /// <summary>
    /// Integration tests for the thinking-block rendering feature that exercise the real
    /// CLI rendering pipeline (FeedView, ThinkingBlockItem, DisplayList) together with
    /// the instrumentation/tracing paths that capture thinking content independently of UI
    /// visibility.
    /// </summary>
    public class ThinkingBlockRenderingIntegrationTests : IDisposable
    {
        private readonly bool _originalVisible;
        private readonly Theme _originalTheme;

        public ThinkingBlockRenderingIntegrationTests()
        {
            _originalVisible = ThinkingView.Visible;
            _originalTheme = Theme.Current;

            // Use a fixed theme so styling assertions are deterministic.
            Theme.Current = Theme.Dark;
        }

        public void Dispose()
        {
            ThinkingView.Visible = _originalVisible;
            Theme.Current = _originalTheme;
        }

        private static (DL.DisplayListBuilder Builder, DL.DisplayList Base) CreateRenderContext()
            => (new DL.DisplayListBuilder(), new DL.DisplayListBuilder().Build());

        private static string[] GetTextRuns(DL.DisplayListBuilder builder)
            => builder.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content).ToArray();

        [Fact]
        public void FeedView_ThinkingVisible_RendersStyledBlock()
        {
            ThinkingView.Visible = true;

            var feed = new FeedView();
            var block = feed.AddThinkingBlock();
            block.AppendContent("Let me consider the project layout");
            block.AppendContent(" and the requested change.");
            block.Complete();

            var (builder, baseDl) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);

            var runs = GetTextRuns(builder);
            Assert.Contains("  [thinking]", runs);
            Assert.Contains("  [end thinking]", runs);
            Assert.Contains(runs, r => r.Contains("Let me consider the project layout"));

            // Indicator lines borrow the theme's ghost/italic treatment.
            var indicator = builder.Build().Ops.OfType<DL.TextRun>()
                .First(r => r.Content == "  [thinking]");
            Assert.Equal(Theme.Current.Ghost, indicator.Fg!.Value);
            Assert.Equal(DL.CellAttrFlags.Italic, indicator.Attrs);
        }

        [Fact]
        public void FeedView_ThinkingHidden_BlocksAreRemovedFromOutput()
        {
            ThinkingView.Visible = false;

            var feed = new FeedView();
            var block = feed.AddThinkingBlock();
            block.AppendContent("This reasoning is hidden from the UI.");
            block.Complete();

            var (builder, baseDl) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);

            var runs = GetTextRuns(builder);
            Assert.DoesNotContain(runs, r => r.Contains("This reasoning is hidden from the UI."));
            Assert.DoesNotContain(runs, r => r.Contains("[thinking]"));
            Assert.DoesNotContain(runs, r => r.Contains("[end thinking]"));
        }

        [Fact]
        public void FeedView_HideThinkingFlag_RemovesBlocksFromOutput()
        {
            // Simulate the --hide-thinking flag path in Program.cs by toggling the
            // process-wide view state. The feed re-measures items on the next render,
            // so existing blocks collapse to zero height.
            ThinkingView.Visible = true;
            var feed = new FeedView();
            feed.AddThinkingBlock().AppendContent("flag-hidden reasoning");

            ThinkingView.Visible = false;
            var (builder, baseDl) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);

            Assert.DoesNotContain(GetTextRuns(builder), r => r.Contains("flag-hidden reasoning"));
        }

        [Fact]
        public void FeedView_AndyShowThinkingEnv_Disabled_HidesBlocks()
        {
            string? previous = Environment.GetEnvironmentVariable("ANDY_SHOW_THINKING");
            try
            {
                Environment.SetEnvironmentVariable("ANDY_SHOW_THINKING", "false");

                // The interactive CLI resolves environment defaults before rendering.
                // Apply the same resolution the shell entrypoint uses.
                bool show = ResolveThinkingVisibilityFromEnvironment();
                ThinkingView.Visible = show;

                var feed = new FeedView();
                feed.AddThinkingBlock().AppendContent("env-hidden reasoning");

                var (builder, baseDl) = CreateRenderContext();
                feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);

                Assert.DoesNotContain(GetTextRuns(builder), r => r.Contains("env-hidden reasoning"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ANDY_SHOW_THINKING", previous);
            }
        }

        [Fact]
        public void FeedView_AndyShowThinkingEnv_Enabled_ShowsBlocks()
        {
            string? previous = Environment.GetEnvironmentVariable("ANDY_SHOW_THINKING");
            try
            {
                Environment.SetEnvironmentVariable("ANDY_SHOW_THINKING", "true");

                ThinkingView.Visible = ResolveThinkingVisibilityFromEnvironment();

                var feed = new FeedView();
                feed.AddThinkingBlock().AppendContent("env-visible reasoning");

                var (builder, baseDl) = CreateRenderContext();
                feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);

                Assert.Contains(GetTextRuns(builder), r => r.Contains("env-visible reasoning"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ANDY_SHOW_THINKING", previous);
            }
        }

        [Fact]
        public void RuntimeToggle_SwitchesVisibilityForExistingBlocks()
        {
            ThinkingView.Visible = true;
            var feed = new FeedView();
            feed.AddThinkingBlock().AppendContent("toggleable reasoning");

            var (builder1, baseDl1) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl1, builder1);
            Assert.Contains(GetTextRuns(builder1), r => r.Contains("toggleable reasoning"));

            bool hidden = ThinkingView.Toggle();
            Assert.False(hidden);

            var (builder2, baseDl2) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl2, builder2);
            Assert.DoesNotContain(GetTextRuns(builder2), r => r.Contains("toggleable reasoning"));

            bool visible = ThinkingView.Toggle();
            Assert.True(visible);

            var (builder3, baseDl3) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl3, builder3);
            Assert.Contains(GetTextRuns(builder3), r => r.Contains("toggleable reasoning"));
        }

        [Fact]
        public void FooterHints_ReflectsThinkingVisibility()
        {
            var visibleHints = FooterHints.Build(false, false, false, true);
            Assert.Contains(visibleHints, h => h.key == "F4" && h.action == "Thinking On");

            var hiddenHints = FooterHints.Build(false, false, false, false);
            Assert.Contains(hiddenHints, h => h.key == "F4" && h.action == "Thinking Off");
        }

        [Fact]
        public async Task ThinkingContent_PreservedInHub_IndependentOfUiVisibility()
        {
            ThinkingView.Visible = false;
            var hub = InstrumentationHub.Instance;
            hub.Clear();

            InstrumentationEvent? received = null;
            using var subscription = hub.Subscribe(evt =>
            {
                received = evt;
                return Task.CompletedTask;
            });

            var thinking = new ThinkingEvent
            {
                Content = "reasoning captured by instrumentation",
                ContentLength = 37,
                Phase = "content"
            };

            hub.Publish(thinking);

            // Engineering tolerance for the fire-and-forget subscriber delivery.
            await Task.Delay(50);

            Assert.NotNull(received);
            var published = Assert.IsType<ThinkingEvent>(received);
            Assert.Equal("reasoning captured by instrumentation", published.Content);
            Assert.Contains(hub.GetEventHistory(), e => e is ThinkingEvent te && te.Content == "reasoning captured by instrumentation");
        }

        [Fact]
        public void ThinkingContent_PreservedInTracer_IndependentOfUiVisibility()
        {
            ThinkingView.Visible = false;
            var tempFile = Path.Combine(Path.GetTempPath(), $"andy-thinking-trace-{Guid.NewGuid():N}.json");
            try
            {
                using (var tracer = new ConversationTracer(enabled: true, customPath: tempFile))
                {
                    tracer.TraceAssistantMessage("assistant reasoning captured regardless of UI toggle");
                }

                var text = File.ReadAllText(tempFile);
                Assert.Contains("assistant_message", text);
                Assert.Contains("assistant reasoning captured regardless of UI toggle", text);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void HiddenThinking_Performance_RemainsReasonableForManyBlocks()
        {
            ThinkingView.Visible = false;
            const int count = 100;
            var feed = new FeedView();

            for (int i = 0; i < count; i++)
            {
                var block = feed.AddThinkingBlock();
                block.AppendContent($"Reasoning chunk {i}: " + new string('x', 500));
                block.Complete();
            }

            feed.SnapToBottom();

            var sw = Stopwatch.StartNew();
            var (builder, baseDl) = CreateRenderContext();
            feed.Render(new L.Rect(0, 0, 80, 24), baseDl, builder);
            sw.Stop();

            var runs = GetTextRuns(builder);
            Assert.DoesNotContain(runs, r => r.Contains("Reasoning chunk"));
            Assert.True(sw.ElapsedMilliseconds < 500,
                $"Rendered {count} hidden thinking blocks in {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Mirrors the environment/flag resolution that Program.cs performs before the first frame.
        /// Keeping this helper in the test assembly lets us exercise the env-var path without
        /// invoking the full interactive TUI.
        /// </summary>
        private static bool ResolveThinkingVisibilityFromEnvironment()
        {
            var showThinkingEnv = Environment.GetEnvironmentVariable("ANDY_SHOW_THINKING");
            if (showThinkingEnv is "false" or "0" or "no")
            {
                return false;
            }
            return true;
        }
    }
}
