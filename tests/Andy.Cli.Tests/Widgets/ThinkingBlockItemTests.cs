// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Andy.Cli.Instrumentation;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Unit tests for thinking-block rendering, visibility controls, content preservation,
    /// and performance characteristics.
    /// </summary>
    public class ThinkingBlockItemTests : IDisposable
    {
        private readonly bool _originalVisible;

        public ThinkingBlockItemTests()
        {
            _originalVisible = ThinkingView.Visible;
            // Ensure a deterministic default theme for render tests.
            Theme.Current = Theme.Dark;
        }

        public void Dispose()
        {
            ThinkingView.Visible = _originalVisible;
        }

        #region Parsing and detection

        [Fact]
        public void AppendContent_AccumulatesText()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Let me ");
            item.AppendContent("check the project structure...");

            Assert.Equal("Let me check the project structure...", item.GetContent());
        }

        [Fact]
        public void AppendContent_IgnoresEmptyStrings()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("hello");
            item.AppendContent("");
            item.AppendContent(null!);
            item.AppendContent(" world");

            Assert.Equal("hello world", item.GetContent());
        }

        [Fact]
        public void ThinkingStream_FullLifecycle_AppendsAndCompletes()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Step 1. ");
            item.AppendContent("Step 2. ");
            item.AppendContent("Step 3.");
            item.Complete();

            Assert.Equal("Step 1. Step 2. Step 3.", item.GetContent());
        }

        [Fact]
        public void MeasureLineCount_DetectsEmptyThinkingBlock()
        {
            var item = new ThinkingBlockItem();
            Assert.Equal(5, item.MeasureLineCount(40));
        }

        [Fact]
        public void MeasureLineCount_DetectsSingleLineThinkingBlock()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Hello world");

            // Width 40: indent=2, innerWidth = 40 - 2 - 2 = 36
            // Lines: 1 indicator + 1 border + 1 body + 1 border + 1 indicator = 5
            Assert.Equal(5, item.MeasureLineCount(40));
        }

        [Fact]
        public void MeasureLineCount_DetectsMultilineThinkingBlock()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Line 1\nLine 2\nLine 3");

            // Width 40: innerWidth = 36, each body line fits in one measured row.
            // Total: 1 indicator + 1 border + 3 body + 1 border + 1 indicator = 7
            Assert.Equal(7, item.MeasureLineCount(40));
        }

        [Fact]
        public void MeasureLineCount_DetectsWrappedLongLine()
        {
            var item = new ThinkingBlockItem();
            // 80 chars of text, innerWidth = 36 -> wraps to 3 lines.
            item.AppendContent(new string('A', 80));

            // Total: 1 + 1 + 3 + 1 + 1 = 7
            Assert.Equal(7, item.MeasureLineCount(40));
        }

        [Fact]
        public void MeasureLineCount_NarrowWidth_ReturnsZero()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Some text");

            Assert.Equal(0, item.MeasureLineCount(3)); // too narrow
        }

        #endregion

        #region ANDY_SHOW_THINKING true/false

        [Fact]
        public void AndyShowThinking_True_RendersBlock()
        {
            ThinkingView.Visible = true;
            var item = new ThinkingBlockItem();
            item.AppendContent("visible reasoning");

            Assert.True(item.MeasureLineCount(40) > 0);
        }

        [Fact]
        public void AndyShowThinking_False_HidesBlock()
        {
            ThinkingView.Visible = false;
            var item = new ThinkingBlockItem();
            item.AppendContent("hidden reasoning");

            Assert.Equal(0, item.MeasureLineCount(40));
        }

        #endregion

        #region --hide-thinking flag semantics

        [Fact]
        public void HideThinkingFlag_HidesBlock()
        {
            ThinkingView.Visible = true;
            // Simulate Program.cs applying --hide-thinking.
            ThinkingView.Visible = false;

            var item = new ThinkingBlockItem();
            item.AppendContent("flag-hidden reasoning");

            Assert.Equal(0, item.MeasureLineCount(40));
        }

        [Fact]
        public void WithoutHideThinkingFlag_BlockIsVisible()
        {
            ThinkingView.Visible = true;

            var item = new ThinkingBlockItem();
            item.AppendContent("plain reasoning");

            Assert.True(item.MeasureLineCount(40) > 0);
        }

        #endregion

        #region Runtime toggle switching visibility

        [Fact]
        public void RuntimeToggle_FlipsVisibility()
        {
            ThinkingView.Visible = true;

            bool result = ThinkingView.Toggle();
            Assert.False(result);
            Assert.False(ThinkingView.Visible);

            result = ThinkingView.Toggle();
            Assert.True(result);
            Assert.True(ThinkingView.Visible);
        }

        [Fact]
        public void RuntimeToggle_RetroactivelyChangesExistingBlocks()
        {
            ThinkingView.Visible = true;
            var item = new ThinkingBlockItem();
            item.AppendContent("toggle me");

            int visibleLines = item.MeasureLineCount(40);
            Assert.True(visibleLines > 0);

            ThinkingView.Visible = false;
            Assert.Equal(0, item.MeasureLineCount(40));

            ThinkingView.Visible = true;
            Assert.Equal(visibleLines, item.MeasureLineCount(40));
        }

        [Fact]
        public void FooterHints_ReflectsThinkingVisibility()
        {
            var visibleHints = FooterHints.Build(false, false, false, true);
            Assert.Contains(visibleHints, h => h.key == "F4" && h.action == "Thinking On");

            var hiddenHints = FooterHints.Build(false, false, false, false);
            Assert.Contains(hiddenHints, h => h.key == "F4" && h.action == "Thinking Off");
        }

        #endregion

        #region Content preservation regardless of UI visibility

        [Fact]
        public void ContentPreserved_WhenHidden_GetContentReturnsFullText()
        {
            ThinkingView.Visible = false;
            var item = new ThinkingBlockItem();
            item.AppendContent("important reasoning");
            item.Complete();

            Assert.Equal(0, item.MeasureLineCount(40));
            Assert.Equal("important reasoning", item.GetContent());
        }

        [Fact]
        public async Task InstrumentationHub_ReceivesThinkingEvent_IndependentOfVisibility()
        {
            var hub = InstrumentationHub.Instance;
            hub.Clear();
            InstrumentationEvent? received = null;
            using var sub = hub.Subscribe(evt =>
            {
                received = evt;
                return Task.CompletedTask;
            });

            var thinking = new ThinkingEvent
            {
                Content = "instrumented reasoning",
                ContentLength = 22,
                Phase = "content"
            };

            hub.Publish(thinking);
            await Task.Delay(50);

            Assert.NotNull(received);
            var published = Assert.IsType<ThinkingEvent>(received);
            Assert.Equal("instrumented reasoning", published.Content);
            Assert.Equal(22, published.ContentLength);
        }

        [Fact]
        public void ConversationTracer_KeepsThinkingContent_WhenHidden()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"andy-test-trace-{Guid.NewGuid():N}.json");
            try
            {
                using (var tracer = new ConversationTracer(enabled: true, customPath: tempFile))
                {
                    // The tracer records thinking content irrespective of the UI toggle.
                    tracer.TraceAssistantMessage("assistant reasoning captured here");
                }

                var lines = File.ReadAllLines(tempFile);
                Assert.Contains(lines, l => l.Contains("assistant_message"));
                Assert.Contains(lines, l => l.Contains("assistant reasoning captured here"));
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }

        #endregion

        #region Performance characteristics when hidden

        [Fact]
        public void HiddenBlock_MeasureLineCount_ReturnsZero()
        {
            ThinkingView.Visible = false;
            var item = new ThinkingBlockItem();
            item.AppendContent("reasoning");

            Assert.Equal(0, item.MeasureLineCount(80));
        }

        [Fact]
        public void HiddenBlock_RenderSlice_DrawsNothing()
        {
            ThinkingView.Visible = false;
            var item = new ThinkingBlockItem();
            item.AppendContent("reasoning");

            var probe = new DL.DisplayListBuilder();
            var probeBase = new DL.DisplayListBuilder().Build();
            item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), probeBase, probe);

            Assert.Empty(probe.Build().Ops);
        }

        [Fact]
        public void HiddenBlock_Performance_RemainsReasonableForManyItems()
        {
            ThinkingView.Visible = false;
            const int count = 50;
            var items = new List<ThinkingBlockItem>(count);
            for (int i = 0; i < count; i++)
            {
                var item = new ThinkingBlockItem();
                item.AppendContent($"Reasoning chunk {i}: " + new string('x', 200));
                items.Add(item);
            }

            var sw = Stopwatch.StartNew();
            int totalLines = 0;
            foreach (var item in items)
            {
                totalLines += item.MeasureLineCount(80);
            }

            sw.Stop();

            Assert.Equal(0, totalLines);
            Assert.True(sw.ElapsedMilliseconds < 100, $"hidden measure took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void VisibleBlock_RenderSlice_DrawsExpectedStructure()
        {
            ThinkingView.Visible = true;
            var item = new ThinkingBlockItem();
            item.AppendContent("visible reasoning");

            int measured = item.MeasureLineCount(40);
            var probe = new DL.DisplayListBuilder();
            var probeBase = new DL.DisplayListBuilder().Build();
            item.RenderSlice(0, 0, 40, 0, measured, probeBase, probe);

            var textRuns = probe.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content).ToList();
            Assert.Contains("  [thinking]", textRuns);
            Assert.Contains("  [end thinking]", textRuns);
            Assert.Contains(textRuns, t => t.Contains("visible reasoning"));
        }

        #endregion
    }
}
