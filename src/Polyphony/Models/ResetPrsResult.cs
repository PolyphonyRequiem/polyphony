namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset prs --apex N</c> — abandons all
/// OPEN polyphony PRs targeting branches under the apex's scope
/// (<c>plan/{N}</c>, <c>mg/{N}-*</c>, <c>impl/{N}-*</c>,
/// <c>evidence/{N}-*</c>, <c>feature/{N}</c>).
///
/// <para>Routing-style envelope: the verb always exits 0; callers route on
/// <see cref="Success"/> + <see cref="Error"/>. Per-PR failures do NOT mark
/// the verb as failed — they show up as entries in <see cref="FailedPrs"/>
/// with the verb still reporting <see cref="Success"/> = true. A true
/// failure (e.g. identity-resolution failure, twig-sync crash) sets
/// <see cref="Success"/> = false and populates <see cref="Error"/>.</para>
/// </summary>
public sealed record ResetPrsResult
{
    /// <summary>Apex root work-item ID (mirrors <c>--apex</c>).</summary>
    public required int Apex { get; init; }

    /// <summary>True when enumeration + abandon completed (per-PR failures permitted).</summary>
    public required bool Success { get; init; }

    /// <summary>True when this was a dry-run preview (no abandons performed).</summary>
    public required bool DryRun { get; init; }

    /// <summary>
    /// Platform-neutral repo slug for diagnostics — e.g.
    /// <c>polyphonyrequiem/polyphony</c> (GitHub) or
    /// <c>org/project/repository</c> (ADO). Empty string when identity
    /// resolution failed.
    /// </summary>
    public string RepoSlug { get; init; } = string.Empty;

    /// <summary>PRs that were successfully abandoned (or would be in dry-run).</summary>
    public IReadOnlyList<ResetAbandonedPr> AbandonedPrs { get; init; } = [];

    /// <summary>PRs the platform reported as un-abandonable (already closed, 404, network, etc.).</summary>
    public IReadOnlyList<ResetFailedPr> FailedPrs { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>One PR that was abandoned (or would be in dry-run).</summary>
public sealed record ResetAbandonedPr
{
    /// <summary>PR number on the platform.</summary>
    public required int Number { get; init; }

    /// <summary>Source branch the PR targets (e.g. <c>plan/62286666</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>Canonical platform web URL for the PR.</summary>
    public required string Url { get; init; }
}

/// <summary>One PR the platform refused to abandon.</summary>
public sealed record ResetFailedPr
{
    /// <summary>PR number on the platform.</summary>
    public required int Number { get; init; }

    /// <summary>Source branch the PR targets.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>Canonical platform web URL for the PR.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// Short diagnostic. Operator-facing — e.g. "platform reported failure",
    /// "exception: ...".
    /// </summary>
    public required string Reason { get; init; }
}
