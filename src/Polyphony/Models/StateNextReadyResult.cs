using Polyphony.Sdlc;

namespace Polyphony;

/// <summary>
/// Output of <c>polyphony state next-ready</c>: which requirement(s) are
/// currently dispatchable for a single work item, plus the full reduced
/// requirement set for diagnostic context.
/// </summary>
/// <remarks>
/// <para>
/// Workflows route on <see cref="Status"/> at the top level; the more granular
/// <see cref="Next"/> / <see cref="Fulfilling"/> / <see cref="Satisfied"/> /
/// <see cref="Needed"/> arrays let workflows pick which requirement to dispatch
/// (or monitor) when multiple are in the same disposition.
/// </para>
/// <para>
/// <see cref="Status"/> values:
/// </para>
/// <list type="bullet">
///   <item><description><c>dispatchable</c>: at least one requirement is at <see cref="Disposition.Ready"/>.</description></item>
///   <item><description><c>monitoring</c>: nothing ready, but at least one requirement is <see cref="Disposition.Fulfilling"/>.</description></item>
///   <item><description><c>blocked</c>: nothing ready, nothing fulfilling, but unsatisfied requirements remain.</description></item>
///   <item><description><c>satisfied</c>: every requirement is <see cref="Disposition.Satisfied"/>.</description></item>
///   <item><description><c>empty</c>: the item has no own-work requirements (e.g. pure organizational container).</description></item>
///   <item><description><c>error</c>: derivation or resolution failed; see <see cref="Error"/>.</description></item>
/// </list>
/// </remarks>
public sealed record StateNextReadyResult
{
    public required int WorkItemId { get; init; }
    public required string WorkItemType { get; init; }

    /// <summary>Top-level routing hint — see remarks above.</summary>
    public required string Status { get; init; }

    /// <summary>Full reduced requirement set with computed dispositions.</summary>
    public required IReadOnlyList<Requirement> Requirements { get; init; }

    /// <summary>Kinds currently at <see cref="Disposition.Ready"/> — dispatch these.</summary>
    public required IReadOnlyList<string> Next { get; init; }

    /// <summary>Kinds currently at <see cref="Disposition.Fulfilling"/> — monitor these.</summary>
    public required IReadOnlyList<string> Fulfilling { get; init; }

    /// <summary>Kinds currently at <see cref="Disposition.Satisfied"/>.</summary>
    public required IReadOnlyList<string> Satisfied { get; init; }

    /// <summary>Kinds currently at <see cref="Disposition.Needed"/> (prerequisites unmet).</summary>
    public required IReadOnlyList<string> Needed { get; init; }

    /// <summary>Per-input provenance for the deriver inputs (decomposable, etc.).</summary>
    public required ResolvedRequirementInputs ResolvedInputs { get; init; }

    /// <summary>True when any deriver input was resolved by inference rather than explicit config.</summary>
    public required bool AnyInputInferred { get; init; }

    /// <summary>Error message when <see cref="Status"/> is <c>error</c>; null otherwise.</summary>
    public string? Error { get; init; }
}
