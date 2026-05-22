namespace Polyphony.Journal.Payloads;

public sealed record PrOpenEvidencePrPayload
{
    public int WorkItemId { get; init; }
    public int RootId { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public string? RepoSlug { get; init; }
    public string? Organization { get; init; }
    public string? Project { get; init; }
    public string? Repository { get; init; }
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public string? Title { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
