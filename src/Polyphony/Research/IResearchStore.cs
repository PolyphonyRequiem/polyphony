namespace Polyphony.Research;

/// <summary>
/// Cross-platform storage abstraction over the sibling research repo.
/// Implementations are selected by the platform router based on
/// <see cref="ResearchDestination.Platform"/>. All sibling-repo writes
/// MUST go through this interface — no direct git, GitHub, or ADO API
/// calls from consumer code.
/// </summary>
public interface IResearchStore
{
    /// <summary>
    /// Write (or overwrite) an artifact at <paramref name="path"/> in the
    /// research repo identified by <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Target repo + branch + root path.</param>
    /// <param name="path">Relative path within <see cref="ResearchDestination.RootPath"/>.</param>
    /// <param name="content">File content (UTF-8 text).</param>
    /// <param name="commitMessage">Commit message for the write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResearchWriteResult> WriteAsync(
        ResearchDestination destination,
        string path,
        string content,
        string commitMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Read the content of an artifact at <paramref name="path"/> in the
    /// research repo. Returns null when the file does not exist.
    /// </summary>
    Task<string?> ReadAsync(
        ResearchDestination destination,
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// List artifacts under <paramref name="prefix"/> in the research repo.
    /// Returns relative paths within <see cref="ResearchDestination.RootPath"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(
        ResearchDestination destination,
        string prefix,
        CancellationToken ct = default);
}
