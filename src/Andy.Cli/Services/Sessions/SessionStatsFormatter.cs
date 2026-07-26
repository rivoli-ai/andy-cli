using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Aggregated usage across a set of sessions. <see cref="TotalCostUsd"/> only sums the
/// sessions whose model has known pricing; <see cref="SessionsWithUnknownPricing"/> counts
/// the rest, so "we do not know" never silently reads as "$0.00".
/// </summary>
public sealed record SessionStats
{
    public int SessionCount { get; init; }
    public int TurnCount { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public decimal? TotalCostUsd { get; init; }
    public int SessionsWithUnknownPricing { get; init; }
    public int SessionsWithoutUsage { get; init; }

    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>True when at least one session contributed a known cost.</summary>
    public bool HasKnownCost => TotalCostUsd is not null;

    /// <summary>True when the reported cost is a lower bound because some pricing is unknown.</summary>
    public bool CostIsPartial => SessionsWithUnknownPricing > 0;
}

/// <summary>
/// Computes and renders the "/session stats" view (issue #285).
/// </summary>
public static class SessionStatsFormatter
{
    public static SessionStats Aggregate(IEnumerable<SessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var list = sessions.ToList();
        var totals = SessionUsage.Empty;
        var unknownPricing = 0;
        var withoutUsage = 0;

        foreach (var session in list)
        {
            var usage = session.Usage;
            if (usage is null || usage.IsEmpty)
            {
                withoutUsage++;
                continue;
            }
            if (usage.PricingUnknown)
            {
                unknownPricing++;
            }
            totals = totals.Add(usage);
        }

        return new SessionStats
        {
            SessionCount = list.Count,
            TurnCount = list.Sum(s => s.TurnCount),
            InputTokens = totals.InputTokens,
            OutputTokens = totals.OutputTokens,
            ReasoningTokens = totals.ReasoningTokens,
            CacheReadTokens = totals.CacheReadTokens,
            CacheWriteTokens = totals.CacheWriteTokens,
            TotalCostUsd = totals.EstimatedCostUsd,
            SessionsWithUnknownPricing = unknownPricing,
            SessionsWithoutUsage = withoutUsage
        };
    }

    /// <summary>Detail view for a single session.</summary>
    public static string FormatSession(SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sb = new StringBuilder();
        sb.Append("Session ").AppendLine(session.SessionId);
        if (!string.IsNullOrEmpty(session.Title))
        {
            sb.Append("  Title:            ").AppendLine(session.Title);
        }
        sb.Append("  Provider/model:   ").Append(session.Provider).Append('/').AppendLine(session.Model);
        sb.Append("  Turns:            ").AppendLine(Number(session.TurnCount));
        if (session.UpdatedUtc != DateTimeOffset.MinValue)
        {
            sb.Append("  Last updated:     ").AppendLine(
                session.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }
        if (session.Lineage is { IsEmpty: false } lineage)
        {
            if (!string.IsNullOrEmpty(lineage.ParentSessionId))
            {
                sb.Append("  Forked from:      ").Append(lineage.ParentSessionId).AppendLine(
                    lineage.ForkedAtTurn is { } turn ? $" (before turn {turn})" : " (full session)");
            }
            if (!string.IsNullOrEmpty(lineage.ImportedFromSessionId))
            {
                sb.Append("  Imported from:    ").AppendLine(lineage.ImportedFromSessionId);
            }
        }
        if (session.Origin is { IsEmpty: false } origin)
        {
            sb.Append("  Recorded in:      ").AppendLine(origin.Describe());
        }

        var usage = session.Usage;
        if (usage is null || usage.IsEmpty)
        {
            sb.AppendLine("  Usage:            not recorded for this session");
            return sb.ToString().TrimEnd();
        }

        sb.Append("  Input tokens:     ").AppendLine(Number(usage.InputTokens));
        sb.Append("  Output tokens:    ").AppendLine(Number(usage.OutputTokens));
        sb.Append("  Reasoning tokens: ").AppendLine(Number(usage.ReasoningTokens));
        sb.Append("  Cache read:       ").AppendLine(Number(usage.CacheReadTokens));
        sb.Append("  Cache write:      ").AppendLine(Number(usage.CacheWriteTokens));
        sb.Append("  Total tokens:     ").AppendLine(Number(usage.TotalTokens));
        sb.Append("  Estimated cost:   ").AppendLine(usage.PricingUnknown
            ? $"unknown (no pricing data for model '{session.Model}')"
            : ModelPricing.FormatUsd(usage.EstimatedCostUsd!.Value));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Totals view across every saved session.</summary>
    public static string FormatTotals(SessionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var sb = new StringBuilder();
        sb.Append("Usage across ").Append(Number(stats.SessionCount)).Append(" session")
          .Append(stats.SessionCount == 1 ? "" : "s").AppendLine(":");
        sb.Append("  Turns:            ").AppendLine(Number(stats.TurnCount));
        sb.Append("  Input tokens:     ").AppendLine(Number(stats.InputTokens));
        sb.Append("  Output tokens:    ").AppendLine(Number(stats.OutputTokens));
        sb.Append("  Reasoning tokens: ").AppendLine(Number(stats.ReasoningTokens));
        sb.Append("  Cache read:       ").AppendLine(Number(stats.CacheReadTokens));
        sb.Append("  Cache write:      ").AppendLine(Number(stats.CacheWriteTokens));
        sb.Append("  Total tokens:     ").AppendLine(Number(stats.TotalTokens));

        if (!stats.HasKnownCost)
        {
            sb.AppendLine("  Estimated cost:   unknown (no pricing data for any session's model)");
        }
        else
        {
            sb.Append("  Estimated cost:   ").Append(ModelPricing.FormatUsd(stats.TotalCostUsd!.Value));
            sb.AppendLine(stats.CostIsPartial
                ? $" (lower bound; {stats.SessionsWithUnknownPricing} session"
                    + (stats.SessionsWithUnknownPricing == 1 ? "" : "s") + " have no pricing data)"
                : "");
        }

        if (stats.SessionsWithoutUsage > 0)
        {
            sb.Append("  Note:             ").Append(Number(stats.SessionsWithoutUsage))
              .Append(" session").Append(stats.SessionsWithoutUsage == 1 ? "" : "s")
              .AppendLine(" recorded no usage (saved before usage tracking existed)");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
