namespace Polyphony;

public sealed record DriftResult
{
    public required string Status { get; init; }
    public required int RootId { get; init; }
    public required DriftFinding[] Findings { get; init; }
    public required DriftSummary Summary { get; init; }
}

public sealed record DriftFinding
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Classification { get; init; }
    public required string ExpectedState { get; init; }
    public string? ActualState { get; init; }
    public bool? PolyphonyOwned { get; init; }
    public string? Platform { get; init; }
    public string? ParentId { get; init; }
}

public sealed record DriftSummary
{
    public required int Consistent { get; init; }
    public required int ExternalDelete { get; init; }
    public required int ExternalMutation { get; init; }
    public required int ExternalCreate { get; init; }
}
