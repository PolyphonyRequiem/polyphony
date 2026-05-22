using Polyphony.Journal;

namespace Polyphony.Tests.TestFixtures;

public static class JournalTestSupport
{
    public static RunContext CreateRunContext(string runId = "test-run") => new(runId);

    public static JournaledActionDecorator CreateDecorator() => new(new NoOpJournalStore());

    private sealed class NoOpJournalStore : IJournalStore
    {
        public string DatabasePath => "journal.db";

        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => Task.FromResult(0L);

        public Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JournalEntry>>([]);

        public Task ExportAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;
    }
}
