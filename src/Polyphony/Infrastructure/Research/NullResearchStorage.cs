namespace Polyphony.Infrastructure.Research;

/// <summary>
/// No-op <see cref="IResearchStorage"/> used when no <c>research:</c>
/// block is configured in <c>profile.yaml</c>. Reads return <c>null</c>
/// and list returns empty — benign degradation. Writes throw
/// <see cref="InvalidOperationException"/> because silently dropping
/// data would hide a configuration mistake.
/// </summary>
public sealed class NullResearchStorage : IResearchStorage
{
    public Task<string?> ReadAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task WriteAsync(string path, string content, string commitMessage, CancellationToken ct = default)
    {
        throw new InvalidOperationException(
            "Research storage is not configured. Add a 'research:' block to " +
            ".polyphony-config/profile.yaml to enable research storage.");
    }

    public Task<IReadOnlyList<string>> ListAsync(string directoryPath, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
