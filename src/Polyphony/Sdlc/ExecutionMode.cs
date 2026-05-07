namespace Polyphony.Sdlc;

/// <summary>
/// Per-type execution-mode constants. Controls how PR #5's edge injection
/// treats relationships between requirements within a single type.
/// </summary>
/// <remarks>
/// <para>
/// PR #4 of the Phase 7 edges arc ships only the schema and resolver
/// surface for this field; <see cref="PlanThenImplement"/> has no behavioral
/// effect until PR #5 wires it into the cross-item edge deriver.
/// </para>
/// <para>
/// Constants-not-enum follows the established polyphony pattern for
/// over-the-wire string vocabularies (see <see cref="Disposition"/>,
/// <see cref="RequirementKind"/>, <see cref="RequirementEdgeSource"/>).
/// </para>
/// </remarks>
public static class ExecutionMode
{
    /// <summary>Default. Requirements within the type may be dispatched in
    /// parallel once their definitional prerequisites are satisfied.</summary>
    public const string Parallel = "parallel";

    /// <summary>Plan must complete before implementation begins. Adds a
    /// synthetic edge from <c>plan_promoted</c> → <c>implementation_merged</c>
    /// at edge-graph build time (PR #5 of the Phase 7 edges arc — not
    /// exercised here).</summary>
    public const string PlanThenImplement = "plan_then_implement";

    /// <summary>Returns true if <paramref name="value"/> is a known
    /// execution mode (<see cref="Parallel"/> or <see cref="PlanThenImplement"/>).
    /// Returns false for null, empty, whitespace, or unknown strings.</summary>
    public static bool IsValid(string? value) =>
        value == Parallel || value == PlanThenImplement;
}
