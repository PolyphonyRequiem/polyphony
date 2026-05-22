using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Projections;

namespace Polyphony.Commands;

[VerbGroup("")]
public sealed class JournalQueryCommand(IJournalStore store, IServiceProvider services)
{
    /// <summary>
    /// Query the root journal's projected resource effects.
    /// </summary>
    /// <param name="root">Root work item ID.</param>
    /// <param name="kind">Optional resource kind filter.</param>
    /// <param name="ownedOnly">When true, only return Polyphony-owned resources.</param>
    /// <param name="state">Optional expected-state filter.</param>
    /// <param name="since">Optional inclusive lower-bound timestamp (ISO-8601).</param>
    /// <param name="until">Optional inclusive upper-bound timestamp (ISO-8601).</param>
    [Command("journal query")]
    [VerbResult(typeof(JournalQueryResult))]
    public async Task<int> Query(
        int root = RequiredInput.MissingInt,
        string? kind = null,
        bool ownedOnly = false,
        string? state = null,
        string? since = null,
        string? until = null,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("journal query", ("--root", root == RequiredInput.MissingInt)) is { } halt)
        {
            return halt;
        }

        if (root <= 0)
        {
            JournalQueryCommandSupport.EmitError("root must be positive");
            return ExitCodes.ConfigError;
        }

        if (!JournalQueryCommandSupport.TryNormalizeKind(kind, out var normalizedKind, out var kindError))
        {
            JournalQueryCommandSupport.EmitError(kindError!);
            return ExitCodes.ConfigError;
        }

        if (!JournalQueryCommandSupport.TryParseTimestampFilter(since, "--since", out var sinceTimestamp, out var sinceError))
        {
            JournalQueryCommandSupport.EmitError(sinceError!);
            return ExitCodes.ConfigError;
        }

        if (!JournalQueryCommandSupport.TryParseTimestampFilter(until, "--until", out var untilTimestamp, out var untilError))
        {
            JournalQueryCommandSupport.EmitError(untilError!);
            return ExitCodes.ConfigError;
        }

        if (sinceTimestamp is { } lower && untilTimestamp is { } upper && lower > upper)
        {
            JournalQueryCommandSupport.EmitError("--since must be less than or equal to --until");
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
                    Since = sinceTimestamp,
                    Until = untilTimestamp,
                },
                ct).ConfigureAwait(false);

            if (!rootLookup.RepositoryAvailable && entries.Count == 0)
            {
                JournalQueryCommandSupport.EmitError(rootLookup.Error ?? "No twig workspace found; root existence could not be verified");
                return ExitCodes.ConfigError;
            }

            var effects = CurrentExpectedState.Project(entries).Resources
                .Where(resource => normalizedKind is null || string.Equals(resource.Kind, normalizedKind, StringComparison.Ordinal))
                .Where(resource => !ownedOnly || resource.PolyphonyOwned)
                .Where(resource => JournalQueryCommandSupport.MatchesState(resource, state))
                .Select(JournalQueryCommandSupport.ToQueryEffect)
                .ToArray();

            var result = new JournalQueryResult
            {
                RootId = root,
                Effects = effects,
                Count = effects.Length,
            };

            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.JournalQueryResult));
            return ExitCodes.Success;
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
