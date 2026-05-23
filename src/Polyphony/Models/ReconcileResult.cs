namespace Polyphony.Models;

/// <summary>
/// W13 (AB#3294): result envelope for <c>polyphony reconcile --root</c>.
/// The verb is a thin lens over <see cref="DriftResult"/> with optional
/// side-effects that write synthesized journal entries under the current
/// lineage so the existing <c>OwnedResources</c> / <c>CurrentExpectedState</c>
/// projections treat reconciled resources as in-scope on the next pass.
/// </summary>
public sealed record ReconcileResult
{
    /// <summary>Root work-item ID the verb was scoped to.</summary>
    public required int Root { get; init; }

    /// <summary>Run id of the synthesized adopt/accept rows (always current lineage).</summary>
    public required string RunId { get; init; }

    /// <summary>True when the verb actually mutated the journal.</summary>
    public required bool Executed { get; init; }

    /// <summary>True when <c>--accept-external</c> was passed.</summary>
    public required bool AcceptExternal { get; init; }

    /// <summary>
    /// Optional <c>KIND:ID</c> selector from <c>--adopt</c>. <c>null</c>
    /// when the operator did not request ownership transfer.
    /// </summary>
    public string? AdoptTarget { get; init; }

    /// <summary>Full drift report — surfaces every finding for transparency.</summary>
    public required DriftResult Drift { get; init; }

    /// <summary>
    /// Findings that <c>--accept-external</c> would (or did) absorb into
    /// the current lineage by recording a <c>reconcile_accept_external</c>
    /// journal row. Always populated, regardless of <c>--execute</c>.
    /// </summary>
    public required IReadOnlyList<ReconciledFinding> AcceptedExternal { get; init; }

    /// <summary>
    /// Findings that <c>--adopt</c> would (or did) attach to the current
    /// lineage via a synthesized <c>reconcile_adopt</c> row. Empty unless
    /// <c>--adopt</c> matched a finding.
    /// </summary>
    public required IReadOnlyList<ReconciledFinding> Adopted { get; init; }

    /// <summary>True when the verb completed without error.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Compact description of a single drift finding that <c>reconcile</c>
/// has acted on (or would act on, in dry-run mode).
/// </summary>
public sealed record ReconciledFinding
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Classification { get; init; }
    public required string ExpectedState { get; init; }
    public string? ActualState { get; init; }
    public required string Action { get; init; }
}
