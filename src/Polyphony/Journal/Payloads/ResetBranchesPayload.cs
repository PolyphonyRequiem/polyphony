namespace Polyphony.Journal.Payloads;

public sealed record ResetBranchesPayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required IReadOnlyList<ResetDeletedBranch> DeletedBranches { get; init; }
    public required IReadOnlyList<ResetFailedBranch> FailedBranches { get; init; }
    public string? Error { get; init; }
}
