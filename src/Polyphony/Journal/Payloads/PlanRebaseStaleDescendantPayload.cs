namespace Polyphony.Journal.Payloads;

public sealed record PlanRebaseStaleDescendantPayload
{
    public required int RootId { get; init; }
    public required int ItemId { get; init; }
    public required int ParentItemId { get; init; }
    public required int PrNumber { get; init; }
    public required string PrUrl { get; init; }
    public required string HeadBranch { get; init; }
    public required string ParentPlanBranch { get; init; }
    public required string Outcome { get; init; }
    public string? OldHeadSha { get; init; }
    public string? NewHeadSha { get; init; }
    public required bool BodyUpdated { get; init; }
    public required bool ManifestRecorded { get; init; }
    public required bool ManifestPushed { get; init; }
    public required bool CommentPosted { get; init; }
    public required IReadOnlyList<string> ConflictFiles { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
