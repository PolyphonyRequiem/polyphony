using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Polyphony.Journal;

public interface IJournalStore : IJournalReader
{
    Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct);
    Task RecordEndAsync(
        long actionId,
        JournalOutcome outcome,
        string? errorCode,
        string? errorMessage,
        string? payloadJson,
        IReadOnlyList<JournalResourceEffect>? effects,
        CancellationToken ct);
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

    public async Task RecordEndAsync(
        long actionId,
        JournalOutcome outcome,
        string? errorCode,
        string? errorMessage,
        string? payloadJson,
        IReadOnlyList<JournalResourceEffect>? effects,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
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

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM journal_effects WHERE journal_entry_id = $id;";
            deleteCommand.Parameters.AddWithValue("$id", actionId);
            await deleteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (effects is not null)
        {
            foreach (var effect in effects)
            {
                await InsertEffectAsync(connection, transaction, actionId, effect, ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await JournalQueryRunner.QueryAsync(connection, query, ct).ConfigureAwait(false);
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
            await ExecuteNonQueryAsync(connection, JournalSchema.EnableForeignKeysPragma, ct).ConfigureAwait(false);
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

    private static async Task InsertEffectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long actionId,
        JournalResourceEffect effect,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO journal_effects(
                journal_entry_id,
                kind,
                resource_id,
                intent,
                mutation,
                polyphony_owned,
                platform,
                parent_id,
                attributes_json)
            VALUES (
                $journalEntryId,
                $kind,
                $resourceId,
                $intent,
                $mutation,
                $polyphonyOwned,
                $platform,
                $parentId,
                $attributesJson);
            """;
        command.Parameters.AddWithValue("$journalEntryId", actionId);
        command.Parameters.AddWithValue("$kind", effect.Kind);
        command.Parameters.AddWithValue("$resourceId", effect.Id);
        command.Parameters.AddWithValue("$intent", ResourceIntentCodec.ToStorage(effect.Intent));
        command.Parameters.AddWithValue("$mutation", ResourceMutationCodec.ToStorage(effect.Mutation));
        command.Parameters.AddWithValue("$polyphonyOwned", effect.PolyphonyOwned ? 1 : 0);
        command.Parameters.AddWithValue("$platform", (object?)effect.Platform ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentId", (object?)effect.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$attributesJson", (object?)SerializeAttributes(effect.Attributes) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? SerializeAttributes(JsonObject? attributes)
        => attributes is null ? null : JsonSerializer.Serialize(attributes, PolyphonyJsonContext.Default.JsonObject);

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
