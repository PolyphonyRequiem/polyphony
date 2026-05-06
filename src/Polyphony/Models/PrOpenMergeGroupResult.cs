namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-mg-pr</c>: opens (or reuses) the pull
/// request that promotes a merge-group branch into its parent. Head is
/// <c>mg/{root}_{mg_path}</c>; base is the parent merge-group branch when
/// nested, or the feature branch when top-level.
/// </summary>
public sealed record PrOpenMergeGroupResult
{
    /// <summary>The PR number assigned by GitHub. Zero when no PR exists yet.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The full PR URL.</summary>
    public required string PrUrl { get; init; }

    /// <summary>The PR title (computed if not supplied as input).</summary>
    public required string Title { get; init; }

    /// <summary>The fully-qualified head branch (e.g. <c>mg/123_core</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The fully-qualified base branch.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The root work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path.</summary>
    public required string MgPath { get; init; }

    /// <summary>True when a new PR was opened; false when an existing open PR was reused.</summary>
    public required bool Created { get; init; }

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}
