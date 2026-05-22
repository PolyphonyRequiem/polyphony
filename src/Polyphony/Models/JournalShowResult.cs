using Polyphony.Journal;

namespace Polyphony;

public sealed record JournalShowResult
{
    public required JournalEntry[] Entries { get; init; }
    public required int Count { get; init; }
    public required JournalShowFilters Filters { get; init; }
}
