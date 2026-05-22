namespace Polyphony.Journal;

public sealed record JournalEntry
{
    public required long Id { get; init; }
    public required string RunId { get; init; }
    public int? RootId { get; init; }
    public int? WorkItemId { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required long StartedAt { get; init; }
    public long? FinishedAt { get; init; }
    public JournalOutcome? Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PayloadJson { get; init; }
    public IReadOnlyList<JournalResourceEffect> Effects { get; init; } = [];
}
