namespace Polyphony.Journal.Payloads;

public sealed record PrCreateFeaturePrPayload
{
    public int WorkItemId { get; init; }
    public required string FeatureBranch { get; init; }
    public required string TargetBranch { get; init; }
    public string? RepoSlug { get; init; }
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public string? Title { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
