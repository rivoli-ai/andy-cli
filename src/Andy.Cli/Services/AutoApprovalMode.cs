using Andy.Permissions.Model;

namespace Andy.Cli.Services;

/// <summary>
/// Holds the interactive "auto-approve" (a.k.a. yolo) mode state for a session and decides whether a
/// given permission request may be allowed without prompting.
///
/// The mode is SESSION-SCOPED only: it is never written to a file-backed permission layer and never
/// survives an application restart on its own. It is enabled explicitly by the user (--auto flag or the
/// /auto toggle) and is surfaced in the status bar while active so it is never silently on.
///
/// Auto-approve is NOT a blanket bypass. When enabled, a request is auto-allowed only if
/// <see cref="ApprovalRiskAssessor"/> judges it <see cref="ApprovalRisk.Normal"/>. Anything
/// <see cref="ApprovalRisk.High"/> (deletes outside the project root, git-repo destruction, database
/// destruction, sensitive paths) still prompts the user every time.
/// </summary>
public sealed class AutoApprovalMode
{
    private readonly string _projectRoot;

    public AutoApprovalMode(string projectRoot)
    {
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? System.IO.Directory.GetCurrentDirectory()
            : projectRoot;
    }

    /// <summary>True while auto-approve is enabled for this session.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Enable auto-approve. Returns the new state (true).</summary>
    public bool Enable() => Enabled = true;

    /// <summary>Disable auto-approve. Returns the new state (false).</summary>
    public bool Disable() => Enabled = false;

    /// <summary>Toggle auto-approve. Returns the new state.</summary>
    public bool Toggle() => Enabled = !Enabled;

    /// <summary>
    /// Decide whether <paramref name="request"/> can be auto-approved right now. Returns true only when
    /// the mode is enabled AND the request is low-risk. High-risk requests always return false so the
    /// caller falls through to the interactive prompt.
    /// </summary>
    public bool CanAutoApprove(PermissionRequest request) =>
        Enabled && ApprovalRiskAssessor.Assess(request, _projectRoot) == ApprovalRisk.Normal;

    /// <summary>
    /// Classify the request for display/auditing. Exposed so the approval record captures the risk level
    /// regardless of whether auto mode is on.
    /// </summary>
    public ApprovalRisk RiskOf(PermissionRequest request) =>
        ApprovalRiskAssessor.Assess(request, _projectRoot);
}
