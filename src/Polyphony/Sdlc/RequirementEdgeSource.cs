namespace Polyphony.Sdlc;

/// <summary>
/// Source provenance for a <see cref="RequirementEdge"/>. Mirrors the
/// dependency-edge taxonomy from the glossary.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description><c>definitional</c>: hard-wired into the requirement model;
///   cannot be overridden. Emitted by <see cref="RequirementSetDeriver"/>.</description></item>
///   <item><description><c>policy</c>: defaults declared in
///   <c>process-config.yaml</c>; resolved per <c>(scope, edge_kind)</c> by
///   <c>polyphony policy resolve</c>. Reserved for later phases.</description></item>
///   <item><description><c>planner_declared</c>: emitted by the planner per item;
///   surfaced in the plan document and reviewed via the plan PR. Reserved for
///   later phases.</description></item>
/// </list>
/// </remarks>
public static class RequirementEdgeSource
{
    public const string Definitional = "definitional";
    public const string Policy = "policy";
    public const string PlannerDeclared = "planner_declared";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the canonical edge source strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Definitional or Policy or PlannerDeclared;
}
