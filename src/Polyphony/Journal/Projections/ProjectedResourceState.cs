using System.Text.Json.Nodes;

namespace Polyphony.Journal.Projections;

public sealed record ProjectedResourceState
{
    public required ResourceKey Key { get; init; }
    public required string Action { get; init; }
    public long? EntryId { get; init; }
    public required long StartedAt { get; init; }
    public required ResourceIntent Intent { get; init; }
    public required ResourceMutation Mutation { get; init; }
    public required bool PolyphonyOwned { get; init; }
    public string? Platform { get; init; }
    public string? ParentId { get; init; }
    public JsonObject? Attributes { get; init; }

    public string Kind => Key.Kind;
    public string Id => Key.Id;
}
