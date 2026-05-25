using Microsoft.Data.Sqlite;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournalReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _journalPath;

    public JournalReaderTests()
    {
        SQLitePCL.Batteries.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _journalPath = Path.Combine(_tempDir, ".polyphony-state", "journal.db");
    }

    [Fact]
    public async Task QueryAsync_Throws_JournalMissingException_When_File_Does_Not_Exist()
    {
        var reader = new JournalReader(_journalPath);

        var ex = await Should.ThrowAsync<JournalMissingException>(
            () => reader.QueryAsync(new JournalQuery(), CancellationToken.None));

        ex.DatabasePath.ShouldBe(reader.DatabasePath);
        File.Exists(_journalPath).ShouldBeFalse(
            "JournalReader must not create the database file when reading missing.");
    }

    [Fact]
    public async Task QueryAsync_Reads_Existing_Journal_Written_By_JournalStore()
    {
        // Arrange: have JournalStore create + populate the journal.
        var writer = new JournalStore(_journalPath);
        var actionId = await writer.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "run-1",
                RootId = 42,
                WorkItemId = 100,
                Action = "test_action",
                Target = "workitem:100",
                StartedAt = 1_000_000_000_000,
                PayloadJson = null,
            },
            CancellationToken.None);

        await writer.RecordEndAsync(
            actionId,
            JournalOutcome.Success,
            errorCode: null,
            errorMessage: null,
            payloadJson: null,
            effects: [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/42",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ],
            CancellationToken.None);

        // Act: read with the read-only reader.
        var reader = new JournalReader(_journalPath);
        var entries = await reader.QueryAsync(new JournalQuery { RootId = 42 }, CancellationToken.None);

        // Assert: same row back, effects loaded.
        entries.Count.ShouldBe(1);
        entries[0].Action.ShouldBe("test_action");
        entries[0].Effects.Count.ShouldBe(1);
        entries[0].Effects[0].Id.ShouldBe("feature/42");
    }

    [Fact]
    public async Task QueryAsync_Read_Is_ReadOnly_Connection()
    {
        // Arrange: create a journal first (so the file exists and is initialized).
        var writer = new JournalStore(_journalPath);
        _ = await writer.QueryAsync(new JournalQuery(), CancellationToken.None);
        File.Exists(_journalPath).ShouldBeTrue();

        var reader = new JournalReader(_journalPath);

        // Act: a read-only connection must refuse writes.
        await using var connection = new SqliteConnection($"Data Source={_journalPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO actions(run_id, action, target, started_at) VALUES ('x','x','x', 0);";

        await Should.ThrowAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        // The reader-issued read still succeeds.
        var entries = await reader.QueryAsync(new JournalQuery(), CancellationToken.None);
        entries.ShouldBeEmpty();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
