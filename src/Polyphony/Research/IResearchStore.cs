namespace Polyphony.Research;

/// <summary>
/// Minimal read/write/list abstraction over a sibling research repository.
/// Implementations are platform-specific (GitHub Contents API, ADO Git API)
/// and selected by <see cref="ResearchStoreFactory"/> based on the resolved
/// <see cref="Configuration.EffectiveResearchConfig.Platform"/>.
/// </summary>
/// <remarks>
/// This is the cross-platform seam described in AB#3072. Consumers call
/// these three operations without knowing whether the backing repo is on
/// GitHub or Azure DevOps. No agent or workflow code yet — this ships
/// only the storage contract.
/// </remarks>
public interface IResearchStore
{
    /// <summary>
    /// Reads the content of a file at <paramref name="path"/> from the
    /// research repo's default branch. Returns <c>null</c> when the file
    /// does not exist.
    /// </summary>
    Task<ResearchEntry?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a file at <paramref name="path"/> in the research
    /// repo. The implementation commits directly to the default branch.
    /// </summary>
    Task<ResearchWriteResult> WriteAsync(
        string path,
        string content,
        string commitMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Lists files under <paramref name="prefix"/> in the research repo.
    /// Returns an empty list when the prefix does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// A single file read from the research repository.
/// </summary>
public sealed record ResearchEntry(
    string Path,
    string Content);

/// <summary>
/// Result of writing a file to the research repository.
/// </summary>
public sealed record ResearchWriteResult(
    bool Success,
    string? CommitSha,
    string? Error);
