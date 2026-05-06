namespace Polyphony.Sdlc;

/// <summary>
/// A single condition the owning work item must satisfy before close-out.
/// AOT-friendly DTO; serialized via <see cref="PolyphonyJsonContext"/>.
/// </summary>
/// <param name="Kind">Canonical kind from <see cref="RequirementKind"/>.</param>
/// <param name="Disposition">Current disposition from <see cref="Sdlc.Disposition"/>.
/// The deriver always emits <c>Needed</c>; downstream consumers compute transitions.</param>
/// <param name="AcceptanceCriteria">Plan-declared, item-specific conditions for
/// <c>Satisfied</c>. <c>null</c> means "use the default for this requirement kind".
/// Populated by the planner per item; the deriver always emits <c>null</c>.</param>
public sealed record Requirement(
    string Kind,
    string Disposition,
    string? AcceptanceCriteria);
