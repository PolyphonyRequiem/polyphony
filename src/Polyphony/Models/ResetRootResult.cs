namespace Polyphony;

public sealed record ResetRootResult
{
    public required int Root { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }
    public required string Strategy { get; init; }
    public required string Coverage { get; init; }
    public required bool FallbackUsed { get; init; }
    public IReadOnlyList<string> StepsCompleted { get; init; } = [];
    public IReadOnlyList<string> StepsFailed { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> AttemptedTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> DeletedTargets { get; init; } = [];
    public IReadOnlyList<FailedResetTargetDescriptor> FailedTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> RemainingResetTargets { get; init; } = [];
    public IReadOnlyList<ResetTargetDescriptor> BlockedMutatedTargets { get; init; } = [];
    public ResetPrsResult? Prs { get; init; }
    public ResetWorktreesResult? Worktrees { get; init; }
    public ResetBranchesResult? Branches { get; init; }
    public ResetFacetsResult? Facets { get; init; }
    public ResetManifestResult? Manifest { get; init; }
    public ResetStateResult? State { get; init; }
    public bool StateSkipped { get; init; }

    /// <summary>
    /// W12 (AB#3293): outcome of the journal-lineage retire step.
    /// Always populated (with <c>RetiredRunIds = []</c>) when reset
    /// runs to success; <c>null</c> when the pipeline halted before
    /// the lineages step or when the journal is the null store.
    /// </summary>
    public Polyphony.Models.LineageRetireResult? Lineages { get; init; }

    public string? Error { get; init; }
}
