namespace Polyphony.Journal;

public sealed record JournalQuery
{
    public int? WorkItemId { get; init; }
    public int? RootId { get; init; }
    public string? RunId { get; init; }
    public string? Action { get; init; }
    public long? Since { get; init; }
    public long? Until { get; init; }
}
