using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Polyphony.Journal;

public interface IJournalStore
{
    string DatabasePath { get; }

    Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct);
    Task RecordEndAsync(
        long actionId,
        JournalOutcome outcome,
        string? errorCode,
        string? errorMessage,
        string? payloadJson,
        IReadOnlyList<JournalResourceEffect>? effects,
        CancellationToken ct);
    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct);
    Task ExportAsync(string destinationPath, CancellationToken ct);

    /// <summary>
    /// W11 (AB#3292): record a (run_id, root_id) lineage row. Idempotent —
    /// repeated calls for the same pair are no-ops. The first observation
    /// wins for <c>created_at</c> / host / user.
    /// </summary>
    Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct);

    /// <summary>
    /// W11 (AB#3292): all lineage rows for the given root, ordered by
    /// <c>created_at</c> ascending. Returns retired and active rows alike;
    /// callers filter via <see cref="JournalLineage.RetiredAt"/>.
    /// </summary>
    Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct);

    /// <summary>
    /// W12 (AB#3293): tombstone a lineage. Sets <c>retired_at</c> /
    /// <c>retired_reason</c>. Returns <c>true</c> if a row was updated.
    /// </summary>
    Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct);
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
        var insertedId = Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);

        // W11 (AB#3292): opportunistically record the lineage. Cheap
        // INSERT OR IGNORE — first observation wins. Only when we
        // actually have a root id; lineage is meaningless without it.
        if (entry.RootId is { } rootIdForLineage)
        {
            await RecordLineageInternalAsync(
                connection,
                entry.RunId,
                rootIdForLineage,
                Environment.MachineName,
                Environment.UserName,
                entry.StartedAt,
                ct).ConfigureAwait(false);
        }

        return insertedId;
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
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
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
                    Effects = [],
                });
            }
        }

        if (results.Count == 0)
        {
            return results;
        }

        var effectsByEntryId = await LoadEffectsAsync(connection, results.Select(entry => entry.Id).ToArray(), ct).ConfigureAwait(false);
        for (var index = 0; index < results.Count; index++)
        {
            var entry = results[index];
            results[index] = entry with
            {
                Effects = effectsByEntryId.TryGetValue(entry.Id, out var entryEffects)
                    ? entryEffects
                    : [],
            };
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

    public async Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (rootId <= 0) throw new ArgumentOutOfRangeException(nameof(rootId), "rootId must be positive.");

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await RecordLineageInternalAsync(
            connection,
            runId,
            rootId,
            host,
            user,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct)
    {
        if (rootId <= 0) throw new ArgumentOutOfRangeException(nameof(rootId), "rootId must be positive.");

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, root_id, created_at, retired_at, retired_reason, created_by_host, created_by_user
            FROM journal_lineages
            WHERE root_id = $rootId
            ORDER BY created_at ASC, run_id ASC;
            """;
        command.Parameters.AddWithValue("$rootId", rootId);

        var results = new List<JournalLineage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new JournalLineage
            {
                RunId = reader.GetString(0),
                RootId = reader.GetInt32(1),
                CreatedAt = reader.GetInt64(2),
                RetiredAt = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RetiredReason = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedByHost = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedByUser = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return results;
    }

    public async Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (rootId <= 0) throw new ArgumentOutOfRangeException(nameof(rootId), "rootId must be positive.");

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE journal_lineages
            SET retired_at = $retiredAt,
                retired_reason = COALESCE($reason, retired_reason)
            WHERE run_id = $runId AND root_id = $rootId AND retired_at IS NULL;
            """;
        command.Parameters.AddWithValue("$retiredAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$rootId", rootId);
        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    private static async Task RecordLineageInternalAsync(
        SqliteConnection connection,
        string runId,
        int rootId,
        string? host,
        string? user,
        long createdAtUnixMs,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // INSERT OR IGNORE: first observation wins. Retirement is a
        // separate UPDATE path; never collapse an existing row.
        command.CommandText = """
            INSERT OR IGNORE INTO journal_lineages(run_id, root_id, created_at, created_by_host, created_by_user)
            VALUES ($runId, $rootId, $createdAt, $host, $user);
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$createdAt", createdAtUnixMs);
        command.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
        command.Parameters.AddWithValue("$user", (object?)user ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static async Task<Dictionary<long, IReadOnlyList<JournalResourceEffect>>> LoadEffectsAsync(
        SqliteConnection connection,
        IReadOnlyList<long> actionIds,
        CancellationToken ct)
    {
        var byEntryId = new Dictionary<long, List<JournalResourceEffect>>();
        if (actionIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            "SELECT journal_entry_id, kind, resource_id, intent, mutation, polyphony_owned, platform, parent_id, attributes_json FROM journal_effects WHERE journal_entry_id IN (");
        for (var index = 0; index < actionIds.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            var parameterName = $"$id{index}";
            sql.Append(parameterName);
            command.Parameters.AddWithValue(parameterName, actionIds[index]);
        }

        sql.Append(") ORDER BY journal_entry_id ASC, id ASC;");
        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var journalEntryId = reader.GetInt64(0);
            var effect = new JournalResourceEffect
            {
                Kind = reader.GetString(1),
                Id = reader.GetString(2),
                Intent = ResourceIntentCodec.Parse(reader.GetString(3)),
                Mutation = ResourceMutationCodec.Parse(reader.GetString(4)),
                PolyphonyOwned = reader.GetInt64(5) != 0,
                Platform = reader.IsDBNull(6) ? null : reader.GetString(6),
                ParentId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Attributes = reader.IsDBNull(8) ? null : DeserializeAttributes(reader.GetString(8)),
            };

            if (!byEntryId.TryGetValue(journalEntryId, out var entryEffects))
            {
                entryEffects = [];
                byEntryId[journalEntryId] = entryEffects;
            }

            entryEffects.Add(effect);
        }

        return byEntryId.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<JournalResourceEffect>)pair.Value);
    }

    private static string? SerializeAttributes(JsonObject? attributes)
        => attributes is null ? null : JsonSerializer.Serialize(attributes, PolyphonyJsonContext.Default.JsonObject);

    private static JsonObject DeserializeAttributes(string json)
        => JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Journal effect attributes must deserialize to a JSON object.");

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
