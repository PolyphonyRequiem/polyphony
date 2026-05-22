using System.Text.Json.Nodes;

namespace Polyphony.Journal.Projections;

public static class CurrentExpectedState
{
    public static CurrentExpectedStateResult Project(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var current = new Dictionary<ResourceKey, ProjectedResourceState>();
        foreach (var entry in entries
            .Where(Contributes)
            .OrderBy(entry => entry.StartedAt)
            .ThenBy(entry => entry.Id))
        {
            foreach (var effect in entry.Effects)
            {
                current[new ResourceKey { Kind = effect.Kind, Id = effect.Id }] = ToProjectedState(effect, entry);
            }
        }

        return new CurrentExpectedStateResult
        {
            Resources = Order(current.Values),
        };
    }

    public static CurrentExpectedStateResult Project(IEnumerable<JournalResourceEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        var current = new Dictionary<ResourceKey, ProjectedResourceState>();
        foreach (var effect in effects)
        {
            current[new ResourceKey { Kind = effect.Kind, Id = effect.Id }] = ToProjectedState(effect);
        }

        return new CurrentExpectedStateResult
        {
            Resources = Order(current.Values),
        };
    }

    internal static ProjectedResourceState ToProjectedState(JournalResourceEffect effect, JournalEntry? entry = null)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return new ProjectedResourceState
        {
            Key = new ResourceKey { Kind = effect.Kind, Id = effect.Id },
            Action = entry?.Action ?? string.Empty,
            EntryId = entry?.Id,
            StartedAt = entry?.StartedAt ?? 0,
            Intent = effect.Intent,
            Mutation = effect.Mutation,
            PolyphonyOwned = effect.PolyphonyOwned,
            Platform = effect.Platform,
            ParentId = effect.ParentId,
            Attributes = effect.Attributes?.DeepClone().AsObject(),
        };
    }

    private static bool Contributes(JournalEntry entry)
        => entry.Outcome is JournalOutcome.Success or JournalOutcome.NoOp;

    private static ProjectedResourceState[] Order(IEnumerable<ProjectedResourceState> resources)
        => resources
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.Id, StringComparer.Ordinal)
            .ToArray();
}

public sealed record CurrentExpectedStateResult
{
    public required ProjectedResourceState[] Resources { get; init; }
}
