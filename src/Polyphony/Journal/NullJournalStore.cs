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
}
