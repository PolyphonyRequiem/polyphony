using System.Text.Json.Nodes;

namespace Polyphony.Journal;

public sealed record JournalResourceEffect
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required ResourceIntent Intent { get; init; }
    public required ResourceMutation Mutation { get; init; }
    public bool PolyphonyOwned { get; init; } = true;
    public string? Platform { get; init; }
    public string? ParentId { get; init; }
    public JsonObject? Attributes { get; init; }
}
