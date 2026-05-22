namespace Polyphony;

public sealed record JournalOwnedResult
{
    public required int RootId { get; init; }
    public required JournalOwnedResource[] OwnedResources { get; init; }
    public required int Count { get; init; }
}

public sealed record JournalOwnedResource
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string ExpectedState { get; init; }
}
