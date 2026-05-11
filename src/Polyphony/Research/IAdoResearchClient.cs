namespace Polyphony.Research;

/// <summary>
/// Platform-specific client for reading/writing files in an Azure DevOps
/// Git repository via the ADO Git Items / Pushes API. Deliberately separate
/// from <see cref="Infrastructure.AzureDevOps.IAdoClient"/> — that interface
/// is PR-centric; this one is content-centric.
/// </summary>
public interface IAdoResearchClient
{
    /// <summary>
    /// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/items?path={path}&amp;versionDescriptor.version={branch}</c>.
    /// Returns the file content, or <c>null</c> when the file does not exist.
    /// </summary>
    Task<AdoFileContent?> GetFileContentAsync(
        string organization,
        string project,
        string repository,
        string path,
        string branch,
        CancellationToken ct = default);

    /// <summary>
    /// <c>POST /{org}/{project}/_apis/git/repositories/{repo}/pushes</c>.
    /// Creates or updates a file by pushing a commit to the specified branch.
    /// </summary>
    Task<AdoWriteResponse> PushFileContentAsync(
        string organization,
        string project,
        string repository,
        string path,
        string content,
        string commitMessage,
        string branch,
        CancellationToken ct = default);

    /// <summary>
    /// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/items?scopePath={prefix}&amp;recursionLevel=OneLevel</c>.
    /// Returns the list of item paths under the prefix, or an empty list
    /// when the prefix does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> ListItemsAsync(
        string organization,
        string project,
        string repository,
        string prefix,
        string branch,
        CancellationToken ct = default);
}

/// <summary>Content returned from the ADO Git Items API.</summary>
public sealed record AdoFileContent(
    string Path,
    string Content);

/// <summary>Response after pushing a file to an ADO Git repository.</summary>
public sealed record AdoWriteResponse(
    bool Success,
    string? CommitSha,
    string? Error);
