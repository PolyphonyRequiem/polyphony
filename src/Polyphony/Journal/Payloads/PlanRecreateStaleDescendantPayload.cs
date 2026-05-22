namespace Polyphony.Journal.Payloads;

public sealed record PlanRecreateStaleDescendantPayload
{
    public required int RootId { get; init; }
    public required int ItemId { get; init; }
    public required int ParentItemId { get; init; }
    public required int OldPrNumber { get; init; }
    public required string OldPrUrl { get; init; }
    public required string OldHeadBranch { get; init; }
    public required string ParentPlanBranch { get; init; }
    public required string Outcome { get; init; }
    public int? NewPrNumber { get; init; }
    public string? NewPrUrl { get; init; }
    public string? NewHeadBranch { get; init; }
    public required bool OldPrClosed { get; init; }
    public required bool OldBranchDeleted { get; init; }
    public required bool NewBranchCreated { get; init; }
    public required bool NewPrOpened { get; init; }
    public required bool ManifestRecorded { get; init; }
    public required bool ManifestPushed { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
