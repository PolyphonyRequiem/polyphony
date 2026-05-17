namespace Polyphony.Models;

/// <summary>
/// Result of <c>polyphony branch mark-impl-merged</c> and
/// <c>polyphony branch clear-impl-merged</c> (AB#3217). Both verbs share
/// the same envelope: they read the current tag set, mutate, push via
/// <c>twig patch</c> + <c>twig sync</c>, and re-read to confirm. The
/// distinction between "I changed it" and "it was already in the desired
/// state" is surfaced via <see cref="AlreadyInDesiredState"/> for caller
/// telemetry — neither value is an error.
/// </summary>
public sealed record BranchImplMergedMarkerResult
{
    /// <summary>
    /// Operation that was attempted — <c>"mark"</c> or <c>"clear"</c>.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Work item the marker was applied to (typically the apex root).
    /// </summary>
    public required int WorkItemId { get; init; }

    /// <summary>
    /// Canonicalized merge-group key embedded in the marker tag
    /// (<see cref="Tagging.PolyphonyTags.NormalizeMergeGroupKey(string)"/>).
    /// Echoed back for caller correlation.
    /// </summary>
    public required string MergeGroupKey { get; init; }

    /// <summary>
    /// The full tag string that was added or removed — e.g.
    /// <c>polyphony:impl-merged-in-mg=pg-1</c>.
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// True when the verb confirmed (via post-sync re-read) that the
    /// item's tag set matches the requested terminal state — tag present
    /// after <c>mark</c>, tag absent after <c>clear</c>.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// True when the input tag set already matched the requested terminal
    /// state and no write was required. Distinct from <see cref="Success"/>
    /// so callers can detect re-entry idempotency.
    /// </summary>
    public required bool AlreadyInDesiredState { get; init; }

    /// <summary>
    /// Populated when <see cref="Success"/> is false. Includes the AB
    /// work item URL so the operator can jump directly to the item
    /// (mirrors the AB#3189/3191 read-after-write diagnostic shape used
    /// by <c>branch next-impl</c>).
    /// </summary>
    public string? Error { get; init; }
}
