namespace Polyphony.Journal;

/// <summary>
/// W11 (AB#3292): a recorded lineage row — a (run_id, root_id) pair
/// that performed at least one journaled action under this checkout.
/// Used by routing, reset, reconcile, and cross-machine attach to
/// answer "which lineages have ever owned this root?" without
/// scanning the entire <c>actions</c> table.
///
/// <para>
/// <see cref="RetiredAt"/> is set by <c>polyphony lineage retire</c>
/// (or by <c>polyphony reset apex</c>'s lineage-retire step) to
/// tombstone a lineage. Retired lineages remain queryable for
/// observability but never anchor an "ours" decision.
/// </para>
/// </summary>
public sealed record JournalLineage
{
    public required string RunId { get; init; }
    public required int RootId { get; init; }

    /// <summary>Unix epoch milliseconds at first observation.</summary>
    public required long CreatedAt { get; init; }

    /// <summary>Unix epoch milliseconds at retirement, or <c>null</c> if active.</summary>
    public long? RetiredAt { get; init; }

    /// <summary>Operator-supplied retirement reason, or <c>null</c>.</summary>
    public string? RetiredReason { get; init; }

    /// <summary>Hostname at first observation, or <c>null</c> if unknown.</summary>
    public string? CreatedByHost { get; init; }

    /// <summary>OS username at first observation, or <c>null</c> if unknown.</summary>
    public string? CreatedByUser { get; init; }
}
