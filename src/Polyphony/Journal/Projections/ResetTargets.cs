using Polyphony.Journal.Observers;

namespace Polyphony.Journal.Projections;

public static class ResetTargets
{
    public static ResetTargetsResult Project(
        IEnumerable<JournalEntry> entries,
        IEnumerable<ObservedResourceState> observations)
        => Project(CurrentExpectedState.Project(entries), observations);

    public static ResetTargetsResult Project(
        IEnumerable<JournalResourceEffect> effects,
        IEnumerable<ObservedResourceState> observations)
        => Project(CurrentExpectedState.Project(effects), observations);

    public static ResetTargetsResult Project(
        CurrentExpectedStateResult currentExpectedState,
        IEnumerable<ObservedResourceState> observations)
    {
        ArgumentNullException.ThrowIfNull(currentExpectedState);
        ArgumentNullException.ThrowIfNull(observations);

        var present = observations
            .Where(observation => observation.Exists)
            .Select(observation => new ResourceKey { Kind = observation.Kind, Id = observation.Id })
            .ToHashSet();

        return new ResetTargetsResult
        {
            Resources = currentExpectedState.Resources
                .Where(resource => resource.PolyphonyOwned)
                .Where(resource => resource.Intent != ResourceIntent.EnsureAbsent)
                .Where(resource => present.Contains(resource.Key))
                .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
                .ThenBy(resource => resource.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }
}

public sealed record ResetTargetsResult
{
    public required ProjectedResourceState[] Resources { get; init; }
}
