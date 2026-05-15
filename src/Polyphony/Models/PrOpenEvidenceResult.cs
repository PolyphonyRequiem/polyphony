namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-evidence-pr</c>: opens (or reuses) the
/// pull request that promotes an evidence branch into its parent
/// <c>feature/&lt;apex&gt;</c> branch (or <c>main</c> for the orphan
/// evidence case where no apex is supplied).
///
/// <para>The verb is platform-aware: when <c>--platform ado</c> is
/// supplied (or the resolver detects an ADO origin), the ADO-specific
/// fields below (<see cref="Organization"/>, <see cref="Project"/>,
/// <see cref="Repository"/>, <see cref="RepoSlug"/>) are populated and
/// the verb internally dispatches to the same logic as
/// <c>polyphony pr open-evidence-ado</c>. On the GitHub leg these
/// fields stay empty, preserving the existing envelope shape.</para>
/// </summary>
public sealed record PrOpenEvidenceResult
{
    /// <summary>The PR number assigned by the platform. Zero when no PR exists yet.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The full PR URL.</summary>
    public required string PrUrl { get; init; }

    /// <summary>The PR title (computed if not supplied as input).</summary>
    public required string Title { get; init; }

    /// <summary>The fully-qualified head branch (e.g. <c>evidence/100-123</c> or <c>evidence/123</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The fully-qualified base branch (e.g. <c>feature/100</c> or <c>main</c>).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The actionable work-item id this evidence PR satisfies.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>The apex (run-root feature) work-item id; equals <see cref="WorkItemId"/> in the orphan-evidence case.</summary>
    public required int ApexId { get; init; }

    /// <summary>True when a new PR was opened; false when an existing open PR was reused.</summary>
    public required bool Created { get; init; }

    /// <summary>ADO organization (populated only on the ADO leg).</summary>
    public string Organization { get; init; } = string.Empty;

    /// <summary>ADO project (populated only on the ADO leg).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>ADO repository name or GUID (populated only on the ADO leg).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Canonical platform-prefixed slug (<c>github.com/{owner}/{name}</c> or <c>dev.azure.com/{org}/{project}/_git/{repo}</c>).</summary>
    public string RepoSlug { get; init; } = string.Empty;

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}

