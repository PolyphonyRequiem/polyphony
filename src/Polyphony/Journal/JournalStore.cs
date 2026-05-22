using System.Text;
using Microsoft.Data.Sqlite;

namespace Polyphony.Journal;

public interface IJournalStore
{
    string DatabasePath { get; }

    Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct);
    Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, CancellationToken ct);
    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct);
    Task ExportAsync(string destinationPath, CancellationToken ct);
}

public sealed class JournalStore : IJournalStore
{
    private readonly string _databasePath;

    public JournalStore(IJournalLocator locator)
        : this(locator.ResolveJournalPath())
    {
    }

    internal JournalStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions(run_id, root_id, work_item_id, action, target, started_at, payload_json)
            VALUES ($runId, $rootId, $workItemId, $action, $target, $startedAt, $payloadJson);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$runId", entry.RunId);
        command.Parameters.AddWithValue("$rootId", (object?)entry.RootId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workItemId", (object?)entry.WorkItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$startedAt", entry.StartedAt);
        command.Parameters.AddWithValue("$payloadJson", (object?)entry.PayloadJson ?? DBNull.Value);

        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE actions
            SET finished_at = $finishedAt,
                outcome = $outcome,
                error_code = $errorCode,
                error_message = $errorMessage,
                payload_json = COALESCE($payloadJson, payload_json)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$outcome", JournalOutcomeCodec.ToStorage(outcome));
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$payloadJson", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", actionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            "SELECT id, run_id, root_id, work_item_id, action, target, started_at, finished_at, outcome, error_code, error_message, payload_json FROM actions WHERE 1 = 1");

        if (query.WorkItemId is { } workItemId)
        {
            sql.Append(" AND work_item_id = $workItemId");
            command.Parameters.AddWithValue("$workItemId", workItemId);
        }

        if (query.RootId is { } rootId)
        {
            sql.Append(" AND root_id = $rootId");
            command.Parameters.AddWithValue("$rootId", rootId);
        }

        if (!string.IsNullOrWhiteSpace(query.RunId))
        {
            sql.Append(" AND run_id = $runId");
            command.Parameters.AddWithValue("$runId", query.RunId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            sql.Append(" AND action = $action");
            command.Parameters.AddWithValue("$action", query.Action);
        }

        if (query.Since is { } since)
        {
            sql.Append(" AND started_at >= $since");
            command.Parameters.AddWithValue("$since", since);
        }

        if (query.Until is { } until)
        {
            sql.Append(" AND started_at <= $until");
            command.Parameters.AddWithValue("$until", until);
        }

        sql.Append(" ORDER BY started_at ASC, id ASC;");
        command.CommandText = sql.ToString();

        var results = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new JournalEntry
            {
                Id = reader.GetInt64(0),
                RunId = reader.GetString(1),
                RootId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                WorkItemId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Action = reader.GetString(4),
                Target = reader.GetString(5),
                StartedAt = reader.GetInt64(6),
                FinishedAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Outcome = reader.IsDBNull(8) ? null : JournalOutcomeCodec.Parse(reader.GetString(8)),
                ErrorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
                PayloadJson = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return results;
    }

    public async Task ExportAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destination))
            File.Delete(destination);

        await using var sourceConnection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var destinationConnection = new SqliteConnection($"Data Source={destination};Mode=ReadWriteCreate;Pooling=False");
        await destinationConnection.OpenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Pooling=False");
        await connection.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(connection, JournalSchema.EnableWalModePragma, ct).ConfigureAwait(false);
            foreach (var statement in JournalSchema.InitializationStatements)
                await ExecuteNonQueryAsync(connection, statement, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
