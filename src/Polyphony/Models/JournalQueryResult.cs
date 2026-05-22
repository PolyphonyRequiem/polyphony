using System.Text.Json.Nodes;
using Polyphony.Journal;

namespace Polyphony;

public sealed record JournalQueryResult
{
    public required int RootId { get; init; }
    public required JournalQueryEffect[] Effects { get; init; }
    public required int Count { get; init; }
}

public sealed record JournalQueryEffect
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string ExpectedState { get; init; }
    public required string Action { get; init; }
    public required long StartedAt { get; init; }
    public required ResourceIntent Intent { get; init; }
    public required ResourceMutation Mutation { get; init; }
    public required bool PolyphonyOwned { get; init; }
    public long? EntryId { get; init; }
    public string? Platform { get; init; }
    public string? ParentId { get; init; }
    public JsonObject? Attributes { get; init; }
}
