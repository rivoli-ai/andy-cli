using System;
using System.Collections.Generic;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Aggregate usage tracking and the /session stats view (issue #285), with particular
/// attention to keeping "unknown pricing" distinct from "costs nothing".
/// </summary>
public class SessionUsageStatsTests : SessionArchiveTestBase
{
    public SessionUsageStatsTests() : base("usage-stats") { }

    private static SessionSummary Summary(string id, SessionUsage? usage, string model = "gpt-4o", int turns = 2) => new()
    {
        SessionId = id,
        Provider = "openai",
        Model = model,
        TurnCount = turns,
        UpdatedUtc = DateTimeOffset.UtcNow,
        Usage = usage
    };

    [Fact]
    public void Aggregate_SumsReasoningAndCacheTokensSeparately()
    {
        var stats = SessionStatsFormatter.Aggregate(new[]
        {
            Summary("a", new SessionUsage
            {
                InputTokens = 1000,
                OutputTokens = 200,
                ReasoningTokens = 50,
                CacheReadTokens = 600,
                CacheWriteTokens = 100,
                EstimatedCostUsd = 0.005m
            }),
            Summary("b", new SessionUsage
            {
                InputTokens = 500,
                OutputTokens = 300,
                ReasoningTokens = 25,
                CacheReadTokens = 100,
                CacheWriteTokens = 10,
                EstimatedCostUsd = 0.004m
            })
        });

        Assert.Equal(1500, stats.InputTokens);
        Assert.Equal(500, stats.OutputTokens);
        Assert.Equal(75, stats.ReasoningTokens);
        Assert.Equal(700, stats.CacheReadTokens);
        Assert.Equal(110, stats.CacheWriteTokens);
        // Cached and reasoning counts are components of input/output, so the headline
        // total must not double count them.
        Assert.Equal(2000, stats.TotalTokens);
        Assert.Equal(0.009m, stats.TotalCostUsd);
        Assert.Equal(0, stats.SessionsWithUnknownPricing);
    }

    [Fact]
    public void UnknownPricing_IsNotTreatedAsZeroCost()
    {
        var known = new SessionUsage { InputTokens = 100, OutputTokens = 100, EstimatedCostUsd = 0.25m };
        var unknown = new SessionUsage { InputTokens = 100, OutputTokens = 100, EstimatedCostUsd = null };

        var stats = SessionStatsFormatter.Aggregate(new[]
        {
            Summary("known", known),
            Summary("unknown", unknown, model: "some-unreleased-model")
        });

        Assert.Equal(0.25m, stats.TotalCostUsd);
        Assert.Equal(1, stats.SessionsWithUnknownPricing);
        Assert.True(stats.CostIsPartial);
        Assert.Contains("lower bound", SessionStatsFormatter.FormatTotals(stats));
    }

    [Fact]
    public void AllPricingUnknown_ReportsUnknownRatherThanZero()
    {
        var stats = SessionStatsFormatter.Aggregate(new[]
        {
            Summary("a", new SessionUsage { InputTokens = 10, OutputTokens = 10 }, "mystery-model")
        });

        Assert.Null(stats.TotalCostUsd);
        Assert.False(stats.HasKnownCost);
        var text = SessionStatsFormatter.FormatTotals(stats);
        Assert.Contains("unknown", text);
        Assert.DoesNotContain("$0.0000", text);
    }

    [Fact]
    public void KnownZeroCost_IsReportedAsZeroNotUnknown()
    {
        // A locally hosted model genuinely costs nothing: that is a KNOWN zero.
        var usage = SessionUsage.FromTokenCounts(5000, 4000).WithEstimatedCost("ollama", "llama3");

        Assert.NotNull(usage.EstimatedCostUsd);
        Assert.Equal(0m, usage.EstimatedCostUsd);
        Assert.False(usage.PricingUnknown);

        var stats = SessionStatsFormatter.Aggregate(new[] { Summary("local", usage, "llama3") });
        Assert.True(stats.HasKnownCost);
        Assert.Contains("$0.0000", SessionStatsFormatter.FormatTotals(stats));
        Assert.Equal(0, stats.SessionsWithUnknownPricing);
    }

    [Fact]
    public void WithEstimatedCost_LeavesUnknownModelsWithoutACost()
    {
        var usage = SessionUsage.FromTokenCounts(1000, 1000)
            .WithEstimatedCost("someprovider", "a-model-nobody-has-priced");

        Assert.Null(usage.EstimatedCostUsd);
        Assert.True(usage.PricingUnknown);
        Assert.Equal("unknown (no pricing data)", usage.FormatCost());
    }

    [Fact]
    public void AddingTwoUnknownCosts_StaysUnknown()
    {
        var a = new SessionUsage { InputTokens = 1 };
        var b = new SessionUsage { OutputTokens = 2 };

        var sum = a.Add(b);

        Assert.Null(sum.EstimatedCostUsd);
        Assert.Equal(1, sum.InputTokens);
        Assert.Equal(2, sum.OutputTokens);
    }

    [Fact]
    public void SessionsWithNoRecordedUsage_AreCountedSeparately()
    {
        var stats = SessionStatsFormatter.Aggregate(new[]
        {
            Summary("legacy", usage: null),
            Summary("modern", new SessionUsage { InputTokens = 10, OutputTokens = 5, EstimatedCostUsd = 0.001m })
        });

        Assert.Equal(1, stats.SessionsWithoutUsage);
        Assert.Equal(10, stats.InputTokens);
        Assert.Contains("recorded no usage", SessionStatsFormatter.FormatTotals(stats));
    }

    [Fact]
    public void Aggregate_OverNoSessions_IsAllZeroAndUnknownCost()
    {
        var stats = SessionStatsFormatter.Aggregate(Array.Empty<SessionSummary>());

        Assert.Equal(0, stats.SessionCount);
        Assert.Equal(0, stats.TotalTokens);
        Assert.Null(stats.TotalCostUsd);
    }

    [Fact]
    public void UsageIsPersistedInTheSessionEnvelopeAndReadBack()
    {
        var id = SessionStore.NewSessionId();
        var usage = new SessionUsage
        {
            InputTokens = 4321,
            OutputTokens = 1234,
            ReasoningTokens = 111,
            CacheReadTokens = 2222,
            CacheWriteTokens = 33,
            EstimatedCostUsd = 0.0421m
        };
        Store.Save(id, SessionArchiveTestData.Snapshot(2), "openai", "gpt-4o",
            new SessionSaveOptions { Usage = usage });

        var reloaded = Store.Load(id)!.Summary.Usage!;
        Assert.Equal(4321, reloaded.InputTokens);
        Assert.Equal(1234, reloaded.OutputTokens);
        Assert.Equal(111, reloaded.ReasoningTokens);
        Assert.Equal(2222, reloaded.CacheReadTokens);
        Assert.Equal(33, reloaded.CacheWriteTokens);
        Assert.Equal(0.0421m, reloaded.EstimatedCostUsd);
    }

    [Fact]
    public void PlainSave_DoesNotEraseUsageRecordedEarlier()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o",
            new SessionSaveOptions { Usage = new SessionUsage { InputTokens = 10, OutputTokens = 5 } });

        // A later save with no options at all (e.g. a code path that never learned about
        // usage) must not silently drop what was already measured.
        Store.Save(id, SessionArchiveTestData.Snapshot(2), "openai", "gpt-4o");

        Assert.Equal(10, Store.Load(id)!.Summary.Usage!.InputTokens);
    }

    [Fact]
    public void UsageSurvivesAnExportImportRoundTrip()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(2), "openai", "gpt-4o",
            new SessionSaveOptions
            {
                Usage = new SessionUsage
                {
                    InputTokens = 900,
                    OutputTokens = 100,
                    ReasoningTokens = 40,
                    CacheReadTokens = 800,
                    CacheWriteTokens = 20,
                    EstimatedCostUsd = 0.00325m
                }
            });
        var export = SessionArchiveExporter.Export(Store, id, WorkPath("usage.json"));

        var result = SessionArchiveImporter.ImportFile(Store, export.Path);
        var usage = Store.Load(result.SessionId)!.Summary.Usage!;

        Assert.Equal(900, usage.InputTokens);
        Assert.Equal(40, usage.ReasoningTokens);
        Assert.Equal(800, usage.CacheReadTokens);
        Assert.Equal(20, usage.CacheWriteTokens);
        Assert.Equal(0.00325m, usage.EstimatedCostUsd);
    }

    [Fact]
    public void FormatSession_NamesTheModelWhenPricingIsUnknown()
    {
        var summary = Summary("a", new SessionUsage { InputTokens = 5, OutputTokens = 5 }, "totally-unknown-model");

        var text = SessionStatsFormatter.FormatSession(summary);

        Assert.Contains("unknown (no pricing data for model 'totally-unknown-model')", text);
    }

    [Fact]
    public void FormatSession_ReportsWhenUsageWasNeverRecorded()
    {
        var text = SessionStatsFormatter.FormatSession(Summary("legacy", usage: null));
        Assert.Contains("not recorded", text);
    }
}
