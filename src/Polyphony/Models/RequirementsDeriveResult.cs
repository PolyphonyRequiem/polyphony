using Polyphony.Sdlc;

namespace Polyphony;

/// <summary>
/// Result envelope for <c>polyphony requirements derive</c>. Wraps the
/// derived <see cref="Sdlc.RequirementSet"/> with the full input context
/// so consumers can audit what was derived and from what.
/// </summary>
public sealed record RequirementsDeriveResult
{
    public required int WorkItemId { get; init; }
    public required string WorkItemType { get; init; }
    public required IReadOnlyList<string> Facets { get; init; }
    public required bool Decomposable { get; init; }
    public IReadOnlyList<string>? FacetOrder { get; init; }
    public string? ActionableExecutor { get; init; }
    public RequirementSet? RequirementSet { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required RequirementsInputProvenance Inputs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Records, per input field, whether the value came from the caller
/// (<c>explicit</c>) or was inferred (<c>inferred</c>). This makes it
/// obvious to consumers which fields the caller is authoritative on.
/// </summary>
public sealed record RequirementsInputProvenance
{
    public required string Decomposable { get; init; }
    public required string FacetOrder { get; init; }
    public required string ActionableExecutor { get; init; }

    public const string Explicit = "explicit";
    public const string Inferred = "inferred";
    public const string NotApplicable = "not_applicable";
}
