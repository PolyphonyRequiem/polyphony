using Polyphony.Configuration;

namespace Polyphony.Research;

/// <summary>
/// <see cref="IResearchStore"/> backed by the GitHub Contents API.
/// Delegates all HTTP work to <see cref="IGitHubResearchClient"/> so
/// retry, timeout, and auth policies are centralized there.
/// </summary>
internal sealed class GitHubResearchStore(
    IGitHubResearchClient client,
    EffectiveResearchConfig config) : IResearchStore
{
    private readonly string _owner = ParseOwner(config.Repository);
    private readonly string _repo = ParseRepo(config.Repository);

    public async Task<ResearchEntry?> ReadAsync(string path, CancellationToken ct)
    {
        var file = await client.GetFileContentAsync(
            _owner, _repo, path, config.DefaultBranch, ct);

        return file is null
            ? null
            : new ResearchEntry(file.Path, file.Content);
    }

    public async Task<ResearchWriteResult> WriteAsync(
        string path,
        string content,
        string commitMessage,
        CancellationToken ct)
    {
        // Read first to get the existing SHA (needed for updates)
        var existing = await client.GetFileContentAsync(
            _owner, _repo, path, config.DefaultBranch, ct);

        var response = await client.PutFileContentAsync(
            _owner, _repo, path, content, commitMessage,
            config.DefaultBranch, existing?.Sha, ct);

        return new ResearchWriteResult(response.Success, response.CommitSha, response.Error);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct) =>
        await client.ListDirectoryAsync(
            _owner, _repo, prefix, config.DefaultBranch, ct);

    private static string ParseOwner(string repository) =>
        repository.Split('/')[0];

    private static string ParseRepo(string repository) =>
        repository.Split('/')[1];
}
