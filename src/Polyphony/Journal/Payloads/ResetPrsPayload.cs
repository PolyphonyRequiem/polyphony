namespace Polyphony.Journal.Payloads;

public sealed record ResetPrsPayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required string RepoSlug { get; init; }
    public required IReadOnlyList<ResetAbandonedPr> AbandonedPrs { get; init; }
    public required IReadOnlyList<ResetFailedPr> FailedPrs { get; init; }
    public string? Error { get; init; }
}
