namespace Polyphony.Journal.Payloads;

public sealed record PlanWritePlanPayload
{
    public required int ItemId { get; init; }
    public required string Path { get; init; }
    public bool PathExistedBefore { get; init; }
    public required bool ContentChanged { get; init; }
    public string? ContentSha256 { get; init; }
    public string? ChildrenPath { get; init; }
    public bool ChildrenPathExistedBefore { get; init; }
    public bool ChildrenChanged { get; init; }
    public bool ChildrenSkipped { get; init; }
    public string? ChildrenSha256 { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
