namespace Polyphony;

public sealed record ResetRootResult
{
    public required int Root { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }
    public required string Coverage { get; init; }
    public IReadOnlyList<string> StepsCompleted { get; init; } = [];
    public IReadOnlyList<string> StepsFailed { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> AttemptedTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> DeletedTargets { get; init; } = [];
    public IReadOnlyList<FailedResetTargetDescriptor> FailedTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> RemainingResetTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> BlockedMutatedTargets { get; init; } = [];
    public bool StateSkipped { get; init; }
    public string? Error { get; init; }
}
