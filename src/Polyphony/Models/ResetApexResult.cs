namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset apex --apex N</c> — the composite
/// reset verb that orchestrates the five primitives in the canonical
/// cleanup order (PRs → worktrees → branches → manifest → state).
///
/// <para>Each step's output is preserved verbatim in the corresponding
/// nested field; the composite verb adds <see cref="StepsCompleted"/> /
/// <see cref="StepsFailed"/> rollups for routing convenience.</para>
///
/// <para>Routing-style envelope: always exits 0. <see cref="Success"/>
/// reflects "every step exited 0 and the per-step <c>Success</c> field is
/// true"; an individual step's per-item failures do NOT mark the apex
/// reset as failed (those surface as entries in <see cref="StepsFailed"/>
/// only when the WHOLE step reported <c>Success</c> = false).</para>
///
/// <para>Step order rationale (per <c>docs/decisions/run-reset.md</c>):</para>
/// <list type="number">
///   <item><c>prs</c> first: abandon open PRs while their head branches
///         still exist (the platform's "PR over a missing branch" is an
///         ambiguous state — easier to close cleanly while everything is
///         intact).</item>
///   <item><c>worktrees</c> before <c>branches</c>: git refuses to delete
///         a branch that's checked out in any worktree. Worktree removal
///         frees the branches for deletion.</item>
///   <item><c>branches</c> next: now that nothing references them, drop
///         every polyphony branch (local + origin) for the apex. This
///         also takes the manifest blob with it (the manifest lives on
///         <c>feature/{N}</c>).</item>
///   <item><c>facets</c> after branches, before manifest: strip the
///         persisted <c>polyphony:facets=*</c> and <c>polyphony:planned</c>
///         tags from the apex subtree. Watermark filtering cannot
///         demote these — they're persisted planning decisions, not PR
///         observations — so without this step the next
///         <c>state classify-lifecycle</c> on a re-dispatched apex
///         silently skips planning (apex 62286666 incident; see
///         <c>docs/decisions/run-reset.md</c>). Must come BEFORE state
///         because state advances the watermark and "clean tags" is
///         part of the watermark-bump invariant.</item>
///   <item><c>manifest</c> after facets: today a read-only inspection
///         pass (the manifest is already gone because branches deleted
///         <c>feature/{N}</c>); PR 3 may extend it to handle
///         partial-reset scenarios.</item>
///   <item><c>state</c> LAST: stamp the new <c>polyphony:run-started-at</c>
///         watermark only after the world is clean. A crash before this
///         step is benign — the operator just reruns reset apex; the
///         only thing not-yet-done is the watermark bump.</item>
/// </list>
/// </summary>
public sealed record ResetApexResult
{
    public required int Apex { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>Ordered list of step names that ran and reported <c>Success</c> = true.</summary>
    public IReadOnlyList<string> StepsCompleted { get; init; } = [];

    /// <summary>Ordered list of step names that ran and reported <c>Success</c> = false.</summary>
    public IReadOnlyList<string> StepsFailed { get; init; } = [];

    /// <summary>Per-step nested results — preserved verbatim from each verb's emitter.</summary>
    public ResetPrsResult? Prs { get; init; }
    public ResetWorktreesResult? Worktrees { get; init; }
    public ResetBranchesResult? Branches { get; init; }
    public ResetFacetsResult? Facets { get; init; }
    public ResetManifestResult? Manifest { get; init; }
    public ResetStateResult? State { get; init; }

    /// <summary>When --skip-state is passed, the state step is intentionally skipped.</summary>
    public bool StateSkipped { get; init; }

    public string? Error { get; init; }
}
