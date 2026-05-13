namespace Polyphony.Infrastructure.Research;

/// <summary>
/// No-op <see cref="IResearchStorage"/> used when no <c>research:</c>
/// block is configured in <c>profile.yaml</c>. Soft-degrades all
/// operations so research is optional infrastructure:
/// <list type="bullet">
///   <item><description>Reads return <c>null</c></description></item>
///   <item><description>List returns empty</description></item>
///   <item><description>Writes complete successfully but discard content,
///     emitting a one-shot stderr warning so operators notice the gap
///     without spamming logs across many archivist articles.</description></item>
/// </list>
/// This contract lets the architect emit <c>research_needs</c> and the
/// cheap researcher run productively even when no sibling repo is wired
/// up — findings still flow into the architect's next iteration; only
/// the persistent archive is sacrificed.
/// </summary>
public sealed class NullResearchStorage : IResearchStorage
{
    private int _writeWarned;

    public Task<string?> ReadAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task WriteAsync(string path, string content, string commitMessage, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref this._writeWarned, 1) == 0)
        {
            Console.Error.WriteLine(
                "warning: research storage is not configured — discarding archivist write " +
                $"to '{path}'. Findings still flow into the current run; persistent archive " +
                "is disabled. Add a 'research:' block to .polyphony-config/profile.yaml " +
                "to enable persistent research storage.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string directoryPath, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
