namespace Polyphony.Journal;

public sealed record JournalEntryStart
{
    public required string RunId { get; init; }
    public int? RootId { get; init; }
    public int? WorkItemId { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required long StartedAt { get; init; }
    public string? PayloadJson { get; init; }
}
