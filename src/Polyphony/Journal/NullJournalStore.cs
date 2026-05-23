namespace Polyphony.Journal;

internal sealed class NullJournalStore : IJournalStore
{
    public string DatabasePath => "journal.db";

    public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => Task.FromResult(0L);

    public Task RecordEndAsync(
        long actionId,
        JournalOutcome outcome,
        string? errorCode,
        string? errorMessage,
        string? payloadJson,
        IReadOnlyList<JournalResourceEffect>? effects,
        CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JournalEntry>>([]);

    public Task ExportAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;

    public Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JournalLineage>>([]);

    public Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct)
        => Task.FromResult(false);
}
