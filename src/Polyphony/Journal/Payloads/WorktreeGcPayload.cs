namespace Polyphony.Journal.Payloads;

public sealed record WorktreeGcPayload
{
    public required bool DryRun { get; init; }
    public required string RunsRoot { get; init; }
    public required int Root { get; init; }
    public required IReadOnlyList<WorktreeGcCandidate> Candidates { get; init; }
    public required int RemovedCount { get; init; }
    public required int FailedCount { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
