namespace Polyphony.Journal.Payloads;

public sealed record PrOpenPlanPrPayload
{
    public int RootId { get; init; }
    public int ItemId { get; init; }
    public int ParentItemId { get; init; }
    public required string ItemKey { get; init; }
    public required bool IsRootPlan { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public string? RepoSlug { get; init; }
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public string? Title { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool Stale { get; init; }
    public string? Error { get; init; }
}
