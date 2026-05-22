using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset;

public sealed class ProjectionResetCoverageAnalyzer(
    IEnumerable<IResourceObserver> observers,
    IEnumerable<IResourceDeleter> deleters)
{
    private readonly IReadOnlyDictionary<string, IResourceObserver> _observers = observers
        .ToDictionary(observer => observer.Kind, StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, IResourceDeleter> _deleters = deleters
        .ToDictionary(deleter => deleter.Kind, StringComparer.Ordinal);

    public ProjectionResetCoverageReport Analyze(IReadOnlyList<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return new ProjectionResetCoverageReport
            {
                Coverage = ProjectionResetCoverage.None,
                MissingEffectsActions = [],
                MissingObservers = [],
                MissingDeleters = [],
            };
        }

        var contributingEntries = entries
            .Where(entry => entry.Outcome is JournalOutcome.Success or JournalOutcome.NoOp)
            .ToArray();
        var missingEffectsActions = contributingEntries
            .Where(entry => entry.Effects.Count == 0)
            .Select(entry => entry.Action)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(action => action, StringComparer.Ordinal)
            .ToArray();

        var relevantKinds = CurrentExpectedState.Project(entries).Resources
            .Where(resource => resource.PolyphonyOwned)
            .Where(ProjectionResetCatalog.IsResetRelevant)
            .Select(resource => resource.Kind)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();

        var missingObservers = relevantKinds
            .Where(kind => !_observers.TryGetValue(kind, out var observer) || !observer.CanObserve)
            .ToArray();
        var missingDeleters = relevantKinds
            .Where(kind => !_deleters.ContainsKey(kind) && !ProjectionResetCatalog.ExcludedKinds.ContainsKey(kind))
            .ToArray();

        return new ProjectionResetCoverageReport
        {
            Coverage = missingEffectsActions.Length == 0
                && missingObservers.Length == 0
                && missingDeleters.Length == 0
                ? ProjectionResetCoverage.Complete
                : ProjectionResetCoverage.Incomplete,
            MissingEffectsActions = missingEffectsActions,
            MissingObservers = missingObservers,
            MissingDeleters = missingDeleters,
        };
    }
}

public sealed record ProjectionResetCoverageReport
{
    public required string Coverage { get; init; }
    public required IReadOnlyList<string> MissingEffectsActions { get; init; }
    public required IReadOnlyList<string> MissingObservers { get; init; }
    public required IReadOnlyList<string> MissingDeleters { get; init; }
}

public static class ProjectionResetCoverage
{
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";
    public const string None = "none";
}
