namespace Polyphony.Research;

/// <summary>
/// Minimal read/write/list abstraction over a sibling research repository.
/// Implementations are platform-specific (GitHub Contents API, ADO Git API);
/// this interface is the cross-platform seam described in AB#3072.
/// Consumers call these three operations without knowing whether the backing
/// repo is on GitHub or Azure DevOps.
/// </summary>
public interface IResearchStore
{
    /// <summary>
    /// Creates or updates a file at <paramref name="path"/> in the research
    /// repo under the given <paramref name="destination"/>. The implementation
    /// commits directly to the default branch.
    /// </summary>
    Task<ResearchWriteResult> WriteAsync(
        ResearchDestination destination,
        string path,
        string content,
        string commitMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the content of a file at <paramref name="path"/> from the
    /// research repo. Returns <c>null</c> when the file does not exist.
    /// </summary>
    Task<string?> ReadAsync(
        ResearchDestination destination,
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Lists files under <paramref name="prefix"/> in the research repo.
    /// Returns an empty list when the prefix does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(
        ResearchDestination destination,
        string prefix,
        CancellationToken ct = default);
}
