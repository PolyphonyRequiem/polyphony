namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset branches --apex N</c> — deletes
/// all polyphony branches for the apex (local + origin) across the prefix
/// set <c>plan/{N}</c>, <c>mg/{N}-*</c>, <c>impl/{N}-*</c>,
/// <c>evidence/{N}-*</c>, <c>feature/{N}</c>.
///
/// <para>Routing-style envelope: always exits 0; per-branch failures show
/// up as entries in <see cref="FailedBranches"/> with the verb still
/// reporting <see cref="Success"/> = true. A true failure (e.g. ls-remote
/// crashed) sets <see cref="Success"/> = false.</para>
/// </summary>
public sealed record ResetBranchesResult
{
    public required int Apex { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>Branches that were successfully deleted (local + origin).</summary>
    public IReadOnlyList<ResetDeletedBranch> DeletedBranches { get; init; } = [];

    /// <summary>Branches the git command refused to delete (e.g. checked out, missing, force-required without --execute).</summary>
    public IReadOnlyList<ResetFailedBranch> FailedBranches { get; init; } = [];

    public string? Error { get; init; }
}

/// <summary>One branch that was deleted (or would be in dry-run).</summary>
public sealed record ResetDeletedBranch
{
    /// <summary>Branch name (no <c>refs/heads/</c> or <c>origin/</c> prefix).</summary>
    public required string Branch { get; init; }

    /// <summary>True when a local copy was present and deleted.</summary>
    public required bool DeletedLocal { get; init; }

    /// <summary>True when an origin copy was present and deleted.</summary>
    public required bool DeletedRemote { get; init; }
}

/// <summary>One branch deletion that failed.</summary>
public sealed record ResetFailedBranch
{
    public required string Branch { get; init; }

    /// <summary>"local" | "remote" | "both"</summary>
    public required string Scope { get; init; }

    /// <summary>Short operator-facing diagnostic.</summary>
    public required string Reason { get; init; }
}
