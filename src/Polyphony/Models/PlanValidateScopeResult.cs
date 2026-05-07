namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan validate-scope &lt;pr-number&gt; --child-scope &lt;glob&gt;...</c>.
/// Routing-style envelope (always exit 0); consumers branch on
/// <see cref="Verdict"/> + <see cref="ErrorCode"/>.
///
/// <para>Phase 3 P8 mechanics — pairs with
/// <see cref="PlanExtractRenegotiationFlagResult"/>. The verdict matrix
/// crosses two axes: did the PR touch any out-of-scope files, and did
/// the PR carry a <c>requests-parent-change</c> renegotiation flag?</para>
///
/// <list type="bullet">
///   <item><c>parent_touched &amp;&amp; !flag</c> → <see cref="Verdict"/> = <c>"block"</c>,
///     <see cref="ErrorCode"/> = <c>scope_violation_no_flag</c>. The
///     child went rogue without declaring intent.</item>
///   <item><c>flag &amp;&amp; !parent_touched</c> → <see cref="Verdict"/> = <c>"allow"</c>,
///     <see cref="Warnings"/> contains <c>flag_without_parent_files</c>.
///     The flag is still legal — the renegotiation may be purely
///     conceptual — but the planner should know.</item>
///   <item><c>flag &amp;&amp; parent_touched</c> → <see cref="Verdict"/> = <c>"allow"</c>.
///     Legitimate scope renegotiation request.</item>
///   <item><c>!flag &amp;&amp; !parent_touched</c> → <see cref="Verdict"/> = <c>"allow"</c>.
///     Normal in-scope plan.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PlanValidateScopeResult
{
    /// <summary>True iff the verb classified successfully. False when an upstream gh/repo error short-circuited the matrix.</summary>
    public required bool Success { get; init; }

    /// <summary>PR number we inspected. Echoed for traceability.</summary>
    public required int PrNumber { get; init; }

    /// <summary>Every file the PR touched (deduped, repo-relative, forward-slash).</summary>
    public IReadOnlyList<string> FilesTouched { get; init; } = Array.Empty<string>();

    /// <summary>Subset of <see cref="FilesTouched"/> that matched at least one supplied <c>--child-scope</c> glob.</summary>
    public IReadOnlyList<string> FilesInScope { get; init; } = Array.Empty<string>();

    /// <summary>Subset of <see cref="FilesTouched"/> that matched none of the supplied <c>--child-scope</c> globs.</summary>
    public IReadOnlyList<string> FilesOutOfScope { get; init; } = Array.Empty<string>();

    /// <summary>True iff the PR body carried at least one well-formed <c>requests-parent-change</c> block.</summary>
    public required bool FlagPresent { get; init; }

    /// <summary>Workflow routing verdict: <c>allow</c> or <c>block</c>.</summary>
    public required string Verdict { get; init; }

    /// <summary>
    /// Non-blocking advisories, e.g. <c>flag_without_parent_files</c>.
    /// Empty when the PR is clean.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Stable machine-readable code on a blocking verdict, or null.
    /// Defined codes: <c>scope_violation_no_flag</c>, <c>pr_not_found</c>,
    /// <c>repo_not_resolved</c>, <c>gh_failed</c>, <c>gh_timeout</c>,
    /// <c>config_error</c>.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error/block detail; null when verdict is allow with no warnings.</summary>
    public string? ErrorMessage { get; init; }
}
