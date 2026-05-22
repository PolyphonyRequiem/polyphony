namespace Polyphony;

public sealed record JournalShowFilters
{
    public int? WorkItem { get; init; }
    public int? Root { get; init; }
    public string? Run { get; init; }
    public string? Action { get; init; }
    public long? Since { get; init; }
    public long? Until { get; init; }
}
