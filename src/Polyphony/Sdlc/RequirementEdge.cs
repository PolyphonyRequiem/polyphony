namespace Polyphony.Sdlc;

/// <summary>
/// A dependency edge between two requirements: <paramref name="DependentKind"/>
/// becomes <c>Ready</c> only when <paramref name="PrerequisiteKind"/> reaches
/// <paramref name="RequiredDisposition"/>.
/// </summary>
/// <param name="PrerequisiteKind">Requirement kind that must reach the threshold first.</param>
/// <param name="DependentKind">Requirement kind whose readiness depends on the prerequisite.</param>
/// <param name="RequiredDisposition">Disposition the prerequisite must reach to release the dependent.
/// Almost always <see cref="Sdlc.Disposition.Satisfied"/> for definitional within-item edges,
/// but the model carries the threshold so plan-gate granularity (release at
/// <c>plan_reviewed</c> vs <c>plan_promoted</c>) can be expressed by later phases.</param>
/// <param name="Source">Provenance from <see cref="RequirementEdgeSource"/>.</param>
public sealed record RequirementEdge(
    string PrerequisiteKind,
    string DependentKind,
    string RequiredDisposition,
    string Source);
