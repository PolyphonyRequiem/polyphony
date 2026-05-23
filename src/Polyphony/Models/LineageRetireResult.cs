namespace Polyphony.Models;

/// <summary>
/// W12 (AB#3293): result envelope for <c>polyphony lineage retire</c>.
/// Reset's tombstone-not-delete posture means future runs can still see
/// what lineages used to exist (for diagnostics, attach, reconcile) while
/// routing verbs treat the retired rows as foreign.
/// </summary>
public sealed record LineageRetireResult
{
    /// <summary>Root work-item ID the verb was scoped to.</summary>
    public required int Root { get; init; }

    /// <summary>
    /// Specific run id requested by the caller, or <c>null</c> when
    /// <c>--all-active</c> was used.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>True when <c>--all-active</c> was passed.</summary>
    public required bool AllActive { get; init; }

    /// <summary>True when the verb actually attempted the retirement (vs. dry-run).</summary>
    public required bool Executed { get; init; }

    /// <summary>Operator-supplied reason (free-form); <c>null</c> when omitted.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Concrete (run_id, root_id) pairs that this invocation tombstoned.
    /// Empty when dry-run, when the lineage was already retired, or
    /// when no matching lineage existed.
    /// </summary>
    public required IReadOnlyList<string> RetiredRunIds { get; init; }

    /// <summary>
    /// Lineages the verb considered but did not retire — already
    /// retired, or not matching the run-id filter. Useful for the
    /// reset pipeline's audit log.
    /// </summary>
    public required IReadOnlyList<string> SkippedRunIds { get; init; }

    /// <summary>True when the verb completed without error.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
