namespace Polyphony.Models;

/// <summary>
/// W14 (AB#3295): result envelope for <c>polyphony lineage attach</c>.
/// On a fresh checkout (no local journal but a root with remote stamped
/// artifacts), the operator can opt-in to a lineage stub so subsequent
/// adoption checks treat the local lineage as current rather than
/// minting a parallel one.
/// </summary>
public sealed record LineageAttachResult
{
    /// <summary>Root work-item ID the verb was scoped to.</summary>
    public required int Root { get; init; }

    /// <summary>Run id of the lineage being stubbed locally.</summary>
    public required string RunId { get; init; }

    /// <summary>True when the verb actually wrote the stub.</summary>
    public required bool Executed { get; init; }

    /// <summary>Operator-supplied reason (free-form); <c>null</c> when omitted.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// True when a matching <c>journal_lineages</c> row already existed
    /// (active or retired) before this invocation. Idempotent surface
    /// for the reset / attach handshake.
    /// </summary>
    public required bool AlreadyExisted { get; init; }

    /// <summary>True when the verb completed without error.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
