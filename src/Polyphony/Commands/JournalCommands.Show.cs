using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;

namespace Polyphony.Commands;

public sealed partial class JournalCommands
{
    /// <summary>
    /// Show journal entries from the current root's action journal.
    /// </summary>
    /// <param name="workItem">Filter to a specific work item id.</param>
    /// <param name="root">Filter to a specific root id.</param>
    /// <param name="run">Filter to a specific run id.</param>
    /// <param name="action">Filter to a specific action name.</param>
    /// <param name="render">Output format: json (default) or text.</param>
    [Command("journal show")]
    [VerbResult(typeof(JournalShowResult))]
    public async Task<int> Show(
        int? workItem = null,
        int? root = null,
        string? run = null,
        string? action = null,
        string render = "json",
        CancellationToken ct = default)
    {
        try
        {
            var query = new JournalQuery
            {
                WorkItemId = workItem,
                RootId = root,
                RunId = Normalize(run),
                Action = Normalize(action),
            };

            var entries = await _store.QueryAsync(query, ct).ConfigureAwait(false);
            if (string.Equals(render, "json", StringComparison.OrdinalIgnoreCase))
            {
                var result = new JournalShowResult
                {
                    Entries = entries.ToArray(),
                    Count = entries.Count,
                    Filters = new JournalShowFilters
                    {
                        WorkItem = workItem,
                        Root = root,
                        Run = Normalize(run),
                        Action = Normalize(action),
                    },
                };
                Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.JournalShowResult));
                return ExitCodes.Success;
            }

            if (string.Equals(render, "text", StringComparison.OrdinalIgnoreCase))
            {
                EmitText(entries);
                return ExitCodes.Success;
            }

            EmitError("render must be 'json' or 'text'");
            return ExitCodes.RoutingFailure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            return ExitCodes.CacheError;
        }
    }

    private static void EmitText(IReadOnlyList<JournalEntry> entries)
    {
        Console.WriteLine("timestamp\taction\ttarget\toutcome\twork_item\troot");
        foreach (var entry in entries)
        {
            Console.WriteLine(string.Join("\t",
                FormatTimestamp(entry.StartedAt),
                entry.Action,
                entry.Target,
                FormatOutcome(entry.Outcome),
                entry.WorkItemId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                entry.RootId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }

    private static string FormatTimestamp(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatOutcome(JournalOutcome? outcome) => outcome switch
    {
        JournalOutcome.Success => "success",
        JournalOutcome.Failure => "failure",
        JournalOutcome.NoOp => "no_op",
        null => string.Empty,
        _ => throw new InvalidOperationException($"Unknown journal outcome '{outcome}'."),
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
