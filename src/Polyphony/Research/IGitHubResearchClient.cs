namespace Polyphony.Research;

/// <summary>
/// Platform-specific client for reading/writing files in a GitHub repository
/// via the GitHub Contents API. Deliberately separate from
/// <see cref="Infrastructure.Processes.IGhClient"/> — that interface is
/// PR-centric; this one is content-centric.
/// </summary>
public interface IGitHubResearchClient
{
    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/contents/{path}?ref={branch}</c>.
    /// Returns the decoded file content, or <c>null</c> when the file
    /// does not exist (404).
    /// </summary>
    Task<GitHubFileContent?> GetFileContentAsync(
        string owner,
        string repo,
        string path,
        string branch,
        CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /repos/{owner}/{repo}/contents/{path}</c>. Creates or updates
    /// a file via the Contents API (auto-commits to the specified branch).
    /// </summary>
    Task<GitHubWriteResponse> PutFileContentAsync(
        string owner,
        string repo,
        string path,
        string content,
        string commitMessage,
        string branch,
        string? existingSha,
        CancellationToken ct = default);

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/contents/{path}?ref={branch}</c> for
    /// a directory. Returns the list of entry names under the path, or
    /// an empty list when the directory does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> ListDirectoryAsync(
        string owner,
        string repo,
        string path,
        string branch,
        CancellationToken ct = default);
}

/// <summary>Content returned from the GitHub Contents API.</summary>
public sealed record GitHubFileContent(
    string Path,
    string Content,
    string Sha);

/// <summary>Response after creating/updating a file via the GitHub Contents API.</summary>
public sealed record GitHubWriteResponse(
    bool Success,
    string? CommitSha,
    string? Error);
