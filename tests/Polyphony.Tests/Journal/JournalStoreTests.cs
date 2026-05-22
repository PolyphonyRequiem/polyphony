using Microsoft.Data.Sqlite;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournalStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public JournalStoreTests()
    {
        SQLitePCL.Batteries.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task QueryAsync_CreatesSchema_AndSchemaVersionRow()
    {
        var entries = await _store.QueryAsync(new JournalQuery(), CancellationToken.None);

        entries.ShouldBeEmpty();
        File.Exists(_store.DatabasePath).ShouldBeTrue();

        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await connection.OpenAsync();

        var objectNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                objectNames.Add(reader.GetString(0));
        }

        objectNames.ShouldContain("actions");
        objectNames.ShouldContain("schema_version");
        objectNames.ShouldContain("idx_actions_work_item");
        objectNames.ShouldContain("idx_actions_root");
        objectNames.ShouldContain("idx_actions_run");
        objectNames.ShouldContain("idx_actions_action");
        objectNames.ShouldContain("idx_actions_started");

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT version FROM schema_version WHERE id = 1;";
        var version = await versionCommand.ExecuteScalarAsync();
        Convert.ToInt32(version).ShouldBe(JournalSchema.CurrentVersion);
    }

    [Fact]
    public async Task RecordStartAndEndAsync_RoundTripsEntry()
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "run-1",
                RootId = 42,
                WorkItemId = 3260,
                Action = "branch_ensure_feature",
                Target = "feature/42",
                StartedAt = 1_717_000_000_000,
                PayloadJson = "{\"phase\":\"start\"}",
            },
            CancellationToken.None);

        await _store.RecordEndAsync(
            actionId,
            JournalOutcome.NoOp,
            errorCode: null,
            errorMessage: null,
            payloadJson: "{\"phase\":\"finish\"}",
            CancellationToken.None);

        var entries = await _store.QueryAsync(new JournalQuery { RootId = 42 }, CancellationToken.None);

        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Id.ShouldBe(actionId);
        entry.RunId.ShouldBe("run-1");
        entry.RootId.ShouldBe(42);
        entry.WorkItemId.ShouldBe(3260);
        entry.Action.ShouldBe("branch_ensure_feature");
        entry.Target.ShouldBe("feature/42");
        entry.StartedAt.ShouldBe(1_717_000_000_000);
        entry.FinishedAt.ShouldNotBeNull();
        entry.Outcome.ShouldBe(JournalOutcome.NoOp);
        entry.ErrorCode.ShouldBeNull();
        entry.ErrorMessage.ShouldBeNull();
        entry.PayloadJson.ShouldBe("{\"phase\":\"finish\"}");
    }

    [Fact]
    public async Task QueryAsync_EnablesWalMode()
    {
        await _store.QueryAsync(new JournalQuery(), CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await command.ExecuteScalarAsync();

        mode.ShouldBe("wal");
    }

    [Fact]
    public async Task QueryAsync_AppliesCombinedFilters()
    {
        await SeedEntryAsync("run-1", rootId: 10, workItemId: 100, action: "branch_ensure_feature", target: "feature/10", startedAt: 1_000);
        await SeedEntryAsync("run-1", rootId: 10, workItemId: 101, action: "pr_open_feature", target: "https://example/pr/1", startedAt: 2_000);
        var expectedId = await SeedEntryAsync("run-2", rootId: 11, workItemId: 100, action: "branch_ensure_feature", target: "feature/11", startedAt: 3_000);

        var entries = await _store.QueryAsync(
            new JournalQuery
            {
                WorkItemId = 100,
                RootId = 11,
                RunId = "run-2",
                Action = "branch_ensure_feature",
                Since = 2_500,
                Until = 3_500,
            },
            CancellationToken.None);

        entries.Count.ShouldBe(1);
        entries[0].Id.ShouldBe(expectedId);
        entries[0].Target.ShouldBe("feature/11");
    }

    private async Task<long> SeedEntryAsync(string runId, int? rootId, int? workItemId, string action, string target, long startedAt)
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = rootId,
                WorkItemId = workItemId,
                Action = action,
                Target = target,
                StartedAt = startedAt,
            },
            CancellationToken.None);

        await _store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, CancellationToken.None);
        return actionId;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
