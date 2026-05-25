using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;

namespace Polyphony.Commands;

[VerbGroup("")]
public sealed class JournalHasCommand(IJournalReader store, IServiceProvider services)
{
    /// <summary>
    /// Check whether the root journal contains a specific action.
    /// </summary>
    /// <param name="root">Root work item ID.</param>
    /// <param name="action">Journal action name to search for.</param>
    /// <param name="target">Optional primary target resource id from the entry's effects.</param>
    /// <param name="run">Optional run id filter.</param>
    [Command("journal has")]
    [VerbResult(typeof(JournalHasResult))]
    public async Task<int> Has(
        int root = RequiredInput.MissingInt,
        string action = "",
        string? target = null,
        string? run = null,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("journal has",
            ("--root", root == RequiredInput.MissingInt),
            ("--action", string.IsNullOrWhiteSpace(action))) is { } halt)
        {
            return halt;
        }

        if (root <= 0)
        {
            JournalQueryCommandSupport.EmitError("root must be positive");
            return ExitCodes.ConfigError;
        }

        try
        {
            var rootLookup = await JournalQueryCommandSupport.LookupRootAsync(services, root, ct).ConfigureAwait(false);
            if (rootLookup.RepositoryAvailable && !rootLookup.Exists)
            {
                JournalQueryCommandSupport.EmitRootNotFound(root);
                return ExitCodes.CacheError;
            }

            var entries = await store.QueryAsync(
                new JournalQuery
                {
                    RootId = root,
                    Action = JournalQueryCommandSupport.Normalize(action),
                    RunId = JournalQueryCommandSupport.Normalize(run),
                },
                ct).ConfigureAwait(false);

            var normalizedTarget = JournalQueryCommandSupport.Normalize(target);
            var matches = entries
                .Where(entry => normalizedTarget is null || JournalQueryCommandSupport.MatchesPrimaryTarget(entry, normalizedTarget))
                .Select(JournalQueryCommandSupport.ToHasMatch)
                .ToArray();

            var result = new JournalHasResult
            {
                Present = matches.Length > 0,
                Matches = matches,
            };

            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.JournalHasResult));
            return result.Present ? ExitCodes.Success : ExitCodes.CacheError;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JournalQueryCommandSupport.EmitError(ex.Message);
            return ExitCodes.CacheError;
        }
    }
}
