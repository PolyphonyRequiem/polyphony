namespace Polyphony.Journal.Projections;

public static class OwnedResources
{
    public static OwnedResourcesResult Project(IEnumerable<JournalEntry> entries)
        => Project(CurrentExpectedState.Project(entries));

    public static OwnedResourcesResult Project(IEnumerable<JournalResourceEffect> effects)
        => Project(CurrentExpectedState.Project(effects));

    public static OwnedResourcesResult Project(CurrentExpectedStateResult currentExpectedState)
    {
        ArgumentNullException.ThrowIfNull(currentExpectedState);

        return new OwnedResourcesResult
        {
            Resources = currentExpectedState.Resources
                .Where(resource => resource.PolyphonyOwned)
                .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
                .ThenBy(resource => resource.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }
}

public sealed record OwnedResourcesResult
{
    public required ProjectedResourceState[] Resources { get; init; }
}
