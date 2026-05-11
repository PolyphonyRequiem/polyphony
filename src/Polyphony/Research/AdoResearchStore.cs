using Polyphony.Configuration;

namespace Polyphony.Research;

/// <summary>
/// <see cref="IResearchStore"/> backed by the Azure DevOps Git API.
/// Delegates all HTTP work to <see cref="IAdoResearchClient"/> so
/// retry, timeout, and auth policies are centralized there.
/// </summary>
internal sealed class AdoResearchStore(
    IAdoResearchClient client,
    EffectiveResearchConfig config) : IResearchStore
{
    private readonly string _organization = ParseSegment(config.Repository, 0);
    private readonly string _project = ParseSegment(config.Repository, 1);
    private readonly string _repository = ParseSegment(config.Repository, 2);

    public async Task<ResearchEntry?> ReadAsync(string path, CancellationToken ct)
    {
        var file = await client.GetFileContentAsync(
            _organization, _project, _repository, path, config.DefaultBranch, ct);

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
        var response = await client.PushFileContentAsync(
            _organization, _project, _repository,
            path, content, commitMessage, config.DefaultBranch, ct);

        return new ResearchWriteResult(response.Success, response.CommitSha, response.Error);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct) =>
        await client.ListItemsAsync(
            _organization, _project, _repository,
            prefix, config.DefaultBranch, ct);

    private static string ParseSegment(string repository, int index) =>
        repository.Split('/')[index];
}
