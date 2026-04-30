namespace Polyphony;

public sealed record HierarchyResult
{
    public required int WorkItemId { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }
    public required string[] Capabilities { get; init; }
    public required string State { get; init; }
    public string? Tags { get; init; }
    public HierarchyResult[]? Children { get; init; }
}
