namespace Polyphony.Journal.Projections;

public sealed record ResourceKey
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
}
