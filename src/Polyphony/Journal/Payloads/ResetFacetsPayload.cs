namespace Polyphony.Journal.Payloads;

public sealed record ResetFacetsPayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required int ItemsScanned { get; init; }
    public required int ItemsModified { get; init; }
    public required int TotalFacetTagsRemoved { get; init; }
    public required int TotalPlannedTagsRemoved { get; init; }
    public required IReadOnlyList<ResetFacetsItem> Items { get; init; }
    public string? Error { get; init; }
}
