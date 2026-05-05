namespace Polyphony.Policy;

/// <summary>
/// Modes for approval gates and PR merge behavior. Symmetric across both domains.
///
/// <list type="bullet">
///   <item><description><see cref="Auto"/> — Approve/merge when quality met OR retry cap reached.
///     Never gates a human, even when the cap is hit without quality. The explicit
///     "approve anyway" semantic.</description></item>
///   <item><description><see cref="Manual"/> — Always require human gate (current default for plan_approval
///     and feature PR merge).</description></item>
///   <item><description><see cref="Warning"/> — Auto-approve clean reviews; gate human only when
///     <c>forced_by_cap=true</c> (retry cap reached without quality).</description></item>
/// </list>
/// </summary>
public enum PolicyMode
{
    Auto,
    Manual,
    Warning,
}

/// <summary>
/// Domains addressable by <c>polyphony policy resolve</c>. Approvals govern the plan-approval gate
/// and downstream review-routing; PR governs the github-pr / feature-pr merge gates;
/// OpenQuestions governs open-question loops in implementation workflows.
/// </summary>
public enum PolicyDomain
{
    Approvals,
    Pr,
    OpenQuestions,
}

/// <summary>
/// Severity threshold for open-question filtering. Parsed case-insensitively from YAML.
/// </summary>
public enum Severity
{
    Low,
    Moderate,
    Major,
    Critical,
}
