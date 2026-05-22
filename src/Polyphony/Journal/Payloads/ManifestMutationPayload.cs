namespace Polyphony.Journal.Payloads;

public sealed record ManifestMutationPayload
{
    public int RootId { get; init; }
    public required string Path { get; init; }
    public string? PathSource { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? PlatformProject { get; init; }
    public string? TopologyHash { get; init; }
    public string? Branch { get; init; }
    public string? Onto { get; init; }
    public string? Reason { get; init; }
    public string? Commit { get; init; }
    public DateTime? RecordedAt { get; init; }
    public string? Gate { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? Detail { get; init; }
    public string? ItemKey { get; init; }
    public int? PreviousGeneration { get; init; }
    public int? CurrentGeneration { get; init; }
    public int? PrNumber { get; init; }
    public string? MergeCommit { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
