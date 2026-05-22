namespace Polyphony.Journal.Payloads;

public sealed record PrVoteAdoPayload
{
    public required string Organization { get; init; }
    public required string Project { get; init; }
    public required string Repository { get; init; }
    public string? RepoSlug { get; init; }
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public required string ReviewerId { get; init; }
    public required string Vote { get; init; }
    public int VoteValue { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
