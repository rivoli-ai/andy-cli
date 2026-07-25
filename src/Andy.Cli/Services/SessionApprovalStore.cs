using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Permissions.Model;

namespace Andy.Cli.Services;

/// <summary>
/// A single recorded approval decision. Every interactive decision the user (or auto mode) makes is
/// appended to the session's approvals file with enough context to audit later and to re-grant the
/// session-scoped rules on resume.
/// </summary>
public sealed record SessionApproval
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Tool { get; init; }
    public string Specifier { get; init; } = string.Empty;
    public required PermissionOutcome Outcome { get; init; }
    public required PersistScope Scope { get; init; }
    public ApprovalRisk Risk { get; init; } = ApprovalRisk.Normal;

    /// <summary>How the decision was reached: "user" (interactive prompt) or "auto" (auto mode).</summary>
    public string Source { get; init; } = "user";

    /// <summary>
    /// Reconstruct the session-layer rule this approval represents, or null when the decision is not a
    /// re-grantable allow (denies, once-scoped decisions, and empty tools yield null).
    /// </summary>
    public PermissionRule? ToSessionRule()
    {
        if (Outcome != PermissionOutcome.Allow || string.IsNullOrWhiteSpace(Tool))
        {
            return null;
        }

        var text = string.IsNullOrEmpty(Specifier) ? $"{Tool}(*)" : $"{Tool}({Specifier})";
        return PermissionRule.TryParse(text, PermissionOutcome.Allow, PermissionLayer.Session, out var rule)
            ? rule
            : null;
    }
}

/// <summary>
/// Persists every approval decision for a session so the grants are not lost on exit and can be restored
/// when the same session is resumed. This closes the gap where interactive approvals were previously
/// session-scoped in memory only and silently vanished on restart.
///
/// Storage layout mirrors <see cref="Sessions.SessionStore"/>: one JSON file per session under
/// ~/.andy/sessions/, named <c>{sessionId}.approvals.json</c> so it travels alongside the transcript
/// file <c>{sessionId}.json</c>. Writes are atomic (temp file + move).
///
/// Lifetime policy (per product decision): session approvals are NEVER deleted by the app - sessions
/// are kept by default, and their approvals are kept with them. Re-granting happens only when the user
/// explicitly resumes that session id.
///
/// Container/headless exception: persistence can be disabled (true "yolo" runs in ephemeral environments
/// leave no on-disk grant trail); the in-memory session rules still apply for the process lifetime.
/// </summary>
public sealed class SessionApprovalStore
{
    public const int SchemaVersion = 1;

    private readonly TimeProvider _clock;

    public SessionApprovalStore(string? directory = null, TimeProvider? clock = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy", "sessions");
        _clock = clock ?? TimeProvider.System;
    }

    public string DirectoryPath { get; }

    /// <summary>
    /// True when approval persistence should be skipped: running headless or inside a container, where
    /// approvals are intentionally ephemeral (true yolo leaves no on-disk grant trail).
    /// </summary>
    public static bool IsEphemeralEnvironment()
    {
        // Headless / CI signal and the standard container markers.
        if (EnvIsTruthy("ANDY_HEADLESS") || EnvIsTruthy("CI"))
        {
            return true;
        }
        if (EnvIsTruthy("DOTNET_RUNNING_IN_CONTAINER") || EnvIsTruthy("CONTAINER"))
        {
            return true;
        }
        try
        {
            if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // Ignore filesystem probe failures; default to non-ephemeral.
        }
        return false;

        static bool EnvIsTruthy(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(v) && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    private string PathFor(string sessionId) => Path.Combine(DirectoryPath, sessionId + ".approvals.json");

    /// <summary>
    /// Append one decision to the session's approvals file. Denies are recorded for audit but are not
    /// re-grantable. Failures never throw into the permission path - recording is best-effort and must
    /// not break tool approval.
    /// </summary>
    public void Record(string sessionId, SessionApproval approval)
    {
        if (!Sessions.SessionStore.IsValidSessionId(sessionId) || approval is null)
        {
            return;
        }

        try
        {
            var all = Load(sessionId).ToList();
            all.Add(approval);
            Save(sessionId, all);
        }
        catch (Exception ex)
        {
            // Recording must never break the approval flow.
            CrashLog.Write("approvals.Record", ex);
        }
    }

    /// <summary>
    /// Record a decision derived directly from a permission request + decision. Convenience overload that
    /// fills tool/specifier from the request's broadened rule form and stamps the time.
    /// </summary>
    public void RecordDecision(
        string sessionId,
        PermissionRequest request,
        PermissionDecision decision,
        ApprovalRisk risk,
        string source)
    {
        var (tool, specifier) = RuleForm(request);
        Record(sessionId, new SessionApproval
        {
            TimestampUtc = _clock.GetUtcNow(),
            Tool = tool,
            Specifier = specifier,
            Outcome = decision.Allowed ? PermissionOutcome.Allow : PermissionOutcome.Deny,
            Scope = decision.Persist,
            Risk = risk,
            Source = source,
        });
    }

    /// <summary>Load all recorded approvals for a session (empty when none). Never throws on a corrupt file.</summary>
    public IReadOnlyList<SessionApproval> Load(string sessionId)
    {
        if (!Sessions.SessionStore.IsValidSessionId(sessionId))
        {
            return Array.Empty<SessionApproval>();
        }

        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return Array.Empty<SessionApproval>();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("approvals", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SessionApproval>();
            }

            var list = new List<SessionApproval>();
            foreach (var el in arr.EnumerateArray())
            {
                var a = ReadApproval(el);
                if (a is not null)
                {
                    list.Add(a);
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            // A corrupt approvals file must not break resume.
            return Array.Empty<SessionApproval>();
        }
    }

    /// <summary>
    /// Load the session's re-grantable allow rules (session-scoped allows only), ready to feed to
    /// <c>IPermissionStore.AddSessionRule</c> on resume. Once-scoped and deny decisions are excluded.
    /// </summary>
    public IReadOnlyList<PermissionRule> LoadGrantableRules(string sessionId)
    {
        var rules = new List<PermissionRule>();
        foreach (var a in Load(sessionId))
        {
            // Only session-scoped (or auto, which is session-scoped) allows are re-granted.
            if (a.Scope != PersistScope.Session)
            {
                continue;
            }
            var rule = a.ToSessionRule();
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }
        return rules;
    }

    private void Save(string sessionId, IReadOnlyList<SessionApproval> approvals)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(sessionId);

        var arr = new JsonArray();
        foreach (var a in approvals)
        {
            arr.Add(new JsonObject
            {
                ["ts"] = a.TimestampUtc.UtcDateTime.ToString("O"),
                ["tool"] = a.Tool,
                ["specifier"] = a.Specifier,
                ["outcome"] = a.Outcome.ToString(),
                ["scope"] = a.Scope.ToString(),
                ["risk"] = a.Risk.ToString(),
                ["source"] = a.Source,
            });
        }

        var envelope = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = sessionId,
            ["approvals"] = arr,
        };

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    private static SessionApproval? ReadApproval(JsonElement el)
    {
        if (!el.TryGetProperty("tool", out var toolEl))
        {
            return null;
        }
        var tool = toolEl.GetString();
        if (string.IsNullOrWhiteSpace(tool))
        {
            return null;
        }

        return new SessionApproval
        {
            Tool = tool,
            TimestampUtc = el.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var t) ? t : DateTimeOffset.MinValue,
            Specifier = el.TryGetProperty("specifier", out var sp) ? sp.GetString() ?? string.Empty : string.Empty,
            Outcome = Enum.TryParse<PermissionOutcome>(el.TryGetProperty("outcome", out var oc) ? oc.GetString() : null, out var o) ? o : PermissionOutcome.Allow,
            Scope = Enum.TryParse<PersistScope>(el.TryGetProperty("scope", out var sc) ? sc.GetString() : null, out var s) ? s : PersistScope.Session,
            Risk = Enum.TryParse<ApprovalRisk>(el.TryGetProperty("risk", out var rk) ? rk.GetString() : null, out var r) ? r : ApprovalRisk.Normal,
            Source = el.TryGetProperty("source", out var so) ? so.GetString() ?? "user" : "user",
        };
    }

    /// <summary>
    /// Reduce a request to the (tool, specifier) form used for the stored rule. Uses the broadened
    /// command-class for commands (same granularity the session-broadening logic grants), so the
    /// re-granted rule matches what the live session would have installed.
    /// </summary>
    private static (string Tool, string Specifier) RuleForm(PermissionRequest request)
    {
        var tool = request?.ToolId ?? string.Empty;
        var resources = request?.Evaluation?.Resources;
        if (resources is not null)
        {
            foreach (var res in resources)
            {
                if (res.Access.Kind == ResourceKind.Command && !string.IsNullOrWhiteSpace(res.Access.Value))
                {
                    var commandClass = CliPermissionPrompt.CommandClass(res.Access.Value);
                    if (!string.IsNullOrEmpty(commandClass))
                    {
                        return (tool, commandClass + ":*");
                    }
                    // No safe broadening: store the exact command so the grant is precise.
                    return (tool, res.Access.Value);
                }
            }
        }
        return (tool, string.Empty);
    }
}
