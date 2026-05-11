using System.Collections.Concurrent;

namespace Polyphony.Research;

/// <summary>
/// In-memory implementation of <see cref="IResearchStore"/> used by tests
/// and the harness. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemoryResearchStore : IResearchStore
{
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Snapshot of all stored files (for test assertions).</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    public Task<ResearchWriteResult> WriteAsync(
        ResearchDestination destination,
        string path,
        string content,
        string commitMessage,
        CancellationToken ct = default)
    {
        var fullPath = CombinePath(destination, path);
        var existed = _files.ContainsKey(fullPath);
        var wasIdentical = existed && _files.TryGetValue(fullPath, out var existing) && existing == content;

        if (wasIdentical)
        {
            return Task.FromResult(new ResearchWriteResult
            {
                Outcome = ResearchWriteResult.Outcomes.NoOp,
                Path = fullPath,
            });
        }

        _files[fullPath] = content;

        return Task.FromResult(new ResearchWriteResult
        {
            Outcome = existed ? ResearchWriteResult.Outcomes.Updated : ResearchWriteResult.Outcomes.Created,
            Path = fullPath,
        });
    }

    public Task<string?> ReadAsync(
        ResearchDestination destination,
        string path,
        CancellationToken ct = default)
    {
        var fullPath = CombinePath(destination, path);
        return Task.FromResult(_files.TryGetValue(fullPath, out var content) ? content : null);
    }

    public Task<IReadOnlyList<string>> ListAsync(
        ResearchDestination destination,
        string prefix,
        CancellationToken ct = default)
    {
        var rootPrefix = string.IsNullOrEmpty(destination.RootPath) ? prefix : $"{destination.RootPath}/{prefix}";
        var matches = _files.Keys
            .Where(k => k.StartsWith(rootPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(matches);
    }

    private static string CombinePath(ResearchDestination destination, string path) =>
        string.IsNullOrEmpty(destination.RootPath) ? path : $"{destination.RootPath}/{path}";
}
