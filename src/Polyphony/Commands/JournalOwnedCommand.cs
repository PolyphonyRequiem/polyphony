using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Projections;

namespace Polyphony.Commands;

[VerbGroup("")]
public sealed class JournalOwnedCommand(IJournalReader store, IServiceProvider services)
{
    /// <summary>
    /// Return the root journal's current Polyphony-owned resources.
    /// </summary>
    /// <param name="root">Root work item ID.</param>
    /// <param name="kind">Optional resource kind filter.</param>
    [Command("journal owned")]
    [VerbResult(typeof(JournalOwnedResult))]
    public async Task<int> Owned(
        int root = RequiredInput.MissingInt,
        string? kind = null,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("journal owned", ("--root", root == RequiredInput.MissingInt)) is { } halt)
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

        try
        {
            var rootLookup = await JournalQueryCommandSupport.LookupRootAsync(services, root, ct).ConfigureAwait(false);
            if (rootLookup.RepositoryAvailable && !rootLookup.Exists)
            {
                JournalQueryCommandSupport.EmitRootNotFound(root);
                return ExitCodes.CacheError;
            }

            var entries = await store.QueryAsync(new JournalQuery { RootId = root }, ct).ConfigureAwait(false);
            if (!rootLookup.RepositoryAvailable && entries.Count == 0)
            {
                JournalQueryCommandSupport.EmitError(rootLookup.Error ?? "No twig workspace found; root existence could not be verified");
                return ExitCodes.ConfigError;
            }
            var ownedResources = OwnedResources.Project(entries).Resources
                .Where(resource => normalizedKind is null || string.Equals(resource.Kind, normalizedKind, StringComparison.Ordinal))
                .Select(JournalQueryCommandSupport.ToOwnedResource)
                .ToArray();

            var result = new JournalOwnedResult
            {
                RootId = root,
                OwnedResources = ownedResources,
                Count = ownedResources.Length,
            };

            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.JournalOwnedResult));
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
