namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-task-pr</c>: opens (or reuses) the pull
/// request that promotes a task branch into its enclosing merge-group
/// branch. Head is <c>task/{root}-{item}</c>; base is
/// <c>mg/{root}_{mg_path}</c>.
/// </summary>
public sealed record PrOpenTaskResult
{
    /// <summary>The PR number assigned by GitHub. Zero when no PR exists yet.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The full PR URL.</summary>
    public required string PrUrl { get; init; }

    /// <summary>The PR title (computed if not supplied as input).</summary>
    public required string Title { get; init; }

    /// <summary>The fully-qualified head branch (e.g. <c>task/123-456</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The fully-qualified base branch (the enclosing merge-group branch).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The root work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>The non-root work-item id.</summary>
    public required int ItemId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path.</summary>
    public required string MgPath { get; init; }

    /// <summary>True when a new PR was opened; false when an existing open PR was reused.</summary>
    public required bool Created { get; init; }

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}
