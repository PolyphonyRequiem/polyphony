using Polyphony.Journal;

namespace Polyphony;

public sealed record JournalHasResult
{
    public required bool Present { get; init; }
    public required JournalHasMatch[] Matches { get; init; }
}

public sealed record JournalHasMatch
{
    public required long EntryId { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required string RunId { get; init; }
    public required long StartedAt { get; init; }
    public int? RootId { get; init; }
    public int? WorkItemId { get; init; }
    public long? FinishedAt { get; init; }
    public JournalOutcome? Outcome { get; init; }
}
