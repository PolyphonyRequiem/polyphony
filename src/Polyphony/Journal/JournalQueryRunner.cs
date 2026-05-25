using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Polyphony.Journal;

/// <summary>
/// Shared read-side SQL for both <see cref="JournalStore"/> (writer +
/// reader) and <see cref="JournalReader"/> (read-only). Lives outside
/// either implementation so the SELECT layout has exactly one definition.
/// </summary>
internal static class JournalQueryRunner
{
    public static async Task<IReadOnlyList<JournalEntry>> QueryAsync(
        SqliteConnection connection,
        JournalQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

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

    private static JsonObject DeserializeAttributes(string json)
        => JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Journal effect attributes must deserialize to a JSON object.");
}
