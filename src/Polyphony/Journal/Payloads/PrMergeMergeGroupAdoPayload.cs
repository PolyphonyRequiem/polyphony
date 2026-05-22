namespace Polyphony.Journal.Payloads;

public sealed record PrMergeMergeGroupAdoPayload
{
    public int RootId { get; init; }
    public required string MergeGroupPath { get; init; }
    public required string Organization { get; init; }
    public required string Project { get; init; }
    public required string Repository { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public string? RepoSlug { get; init; }
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public string? Method { get; init; }
    public bool DeleteBranch { get; init; }
    public string? MergeCommit { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool AlreadyMerged { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
