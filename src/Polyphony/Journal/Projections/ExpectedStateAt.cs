namespace Polyphony.Journal.Projections;

public static class ExpectedStateAt
{
    public static ExpectedStateAtResult Project(IEnumerable<JournalEntry> entries, long until)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var projected = CurrentExpectedState.Project(entries.Where(entry => entry.StartedAt <= until));
        return new ExpectedStateAtResult
        {
            Until = until,
            Resources = projected.Resources,
        };
    }
}

public sealed record ExpectedStateAtResult
{
    public required long Until { get; init; }
    public required ProjectedResourceState[] Resources { get; init; }
}
