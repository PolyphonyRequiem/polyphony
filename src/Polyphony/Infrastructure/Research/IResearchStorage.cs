namespace Polyphony.Infrastructure.Research;

/// <summary>
/// Abstraction for reading, writing, and listing files in a sibling
/// research repository. Implementations handle platform-specific
/// API details (GitHub Contents API via <c>gh api</c>).
///
/// <para>
/// Auth: the default implementation reuses the platform-router's
/// existing credential resolution (GH_TOKEN via
/// <see cref="Processes.GhTokenResolver"/>). An optional per-config
/// auth override scopes a different token to the child process
/// environment without mutating the parent process.
/// </para>
/// </summary>
public interface IResearchStorage
{
    /// <summary>
    /// Read a file from the research repository. Returns the decoded
    /// UTF-8 content, or <c>null</c> when the file does not exist.
    /// </summary>
    /// <param name="path">
    /// Repo-relative path (forward-slash separated). Automatically
    /// prefixed with the configured <c>base_path</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Create or update a file in the research repository.
    /// Handles the SHA dance for existing files automatically.
    /// </summary>
    /// <param name="path">
    /// Repo-relative path (forward-slash separated). Automatically
    /// prefixed with the configured <c>base_path</c>.
    /// </param>
    /// <param name="content">UTF-8 content to write.</param>
    /// <param name="commitMessage">Commit message for the write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(string path, string content, string commitMessage, CancellationToken ct = default);

    /// <summary>
    /// List files in a directory within the research repository.
    /// Returns file paths relative to <c>base_path</c>.
    /// </summary>
    /// <param name="directoryPath">
    /// Directory path relative to <c>base_path</c>. Empty string lists
    /// the root of <c>base_path</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListAsync(string directoryPath, CancellationToken ct = default);
}
