namespace Polyphony.Sdlc;

/// <summary>
/// The complete set of requirements an item must satisfy before close-out,
/// with the dependency edges that order their readiness.
/// </summary>
/// <param name="Items">All requirements, in derivation order. May be empty for
/// pure organizational containers (empty facet set + decomposable=true; their
/// satisfaction is derived from children only — a cross-item concern).</param>
/// <param name="Edges">Dependency edges between requirements in <paramref name="Items"/>.
/// Within-item only at this layer; cross-item edges are added by later phases.</param>
public sealed record RequirementSet(
    IReadOnlyList<Requirement> Items,
    IReadOnlyList<RequirementEdge> Edges);
