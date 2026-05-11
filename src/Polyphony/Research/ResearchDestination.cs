namespace Polyphony.Research;

/// <summary>
/// Identifies the target location in a sibling research repository where
/// promoted artifacts are written. Carries enough context for both GitHub
/// and Azure DevOps platform legs to resolve the write target without
/// leaking platform details into the caller.
/// </summary>
public sealed record ResearchDestination
{
    /// <summary>
    /// Platform identifier: <c>"github"</c> or <c>"azure_devops"</c>.
    /// Used by <see cref="IResearchStore"/> router to select the
    /// platform-specific implementation.
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// For GitHub: <c>owner/repo</c> slug (e.g. <c>"polyphonyrequiem/research"</c>).
    /// For ADO: <c>org/project/repo</c> triple (e.g. <c>"polyphonyrequiem/Polyphony/research"</c>).
    /// </summary>
    public required string RepoLocator { get; init; }

    /// <summary>Target branch in the research repo.</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// Root path within the repo under which promoted artifacts are written
    /// (e.g. <c>"articles"</c>). May be empty for repo-root writes.
    /// </summary>
    public string RootPath { get; init; } = string.Empty;
}
