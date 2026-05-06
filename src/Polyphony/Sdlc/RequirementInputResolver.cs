using Polyphony.Configuration;

namespace Polyphony.Sdlc;

/// <summary>
/// Resolves the per-item inputs the <see cref="RequirementSetDeriver"/> needs
/// (<c>decomposable</c>, <c>facet_order</c>, <c>actionable_executor</c>) from the
/// <see cref="TypeConfig"/> + observable signals (existing children, etc.) when
/// the configuration does not declare them explicitly.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>stop-gap inference layer</b> for Phase 2b. The planner-declared
/// per-instance authority intended by the glossary lives in Phase 7's worklist
/// work; until then, this resolver chooses sensible defaults and surfaces
/// provenance (<see cref="ResolutionProvenance.Explicit"/> vs
/// <see cref="ResolutionProvenance.Inferred"/>) so consumers know when inference
/// was used.
/// </para>
/// <para>
/// Inference rules for <c>decomposable</c>, in priority order:
/// </para>
/// <list type="number">
///   <item><description><see cref="TypeConfig.Decomposable"/> set explicitly → use it.</description></item>
///   <item><description>Item already has children → <c>true</c>.</description></item>
///   <item><description><see cref="TypeConfig.AllowedChildTypes"/> non-empty → <c>true</c>.</description></item>
///   <item><description><see cref="TypeConfig.DecompositionGuidance"/> non-empty → <c>true</c>.</description></item>
///   <item><description>Otherwise → <c>false</c>.</description></item>
/// </list>
/// <para>
/// <c>facet_order</c> is taken from <see cref="TypeConfig.FacetOrder"/> when set,
/// otherwise left null (the deriver requires it only when both actionable and
/// implementable are present).
/// </para>
/// <para>
/// <c>actionable_executor</c> is taken from <see cref="TypeConfig.ActionableExecutor"/>;
/// no inference — actionable is a forward-looking facet not used by current types.
/// </para>
/// </remarks>
public static class RequirementInputResolver
{
    /// <summary>Resolve inputs for an item; <paramref name="childCount"/> is the
    /// observed number of children (used for the decomposable inference fallback).</summary>
    public static ResolvedRequirementInputs Resolve(TypeConfig type, int childCount)
    {
        ArgumentNullException.ThrowIfNull(type);

        var (decomposable, decomposableProvenance) = ResolveDecomposable(type, childCount);
        return new ResolvedRequirementInputs
        {
            Decomposable = decomposable,
            DecomposableProvenance = decomposableProvenance,
            FacetOrder = type.FacetOrder,
            FacetOrderProvenance = type.FacetOrder is { Length: > 0 }
                ? ResolutionProvenance.Explicit
                : ResolutionProvenance.NotApplicable,
            ActionableExecutor = type.ActionableExecutor,
            ActionableExecutorProvenance = type.ActionableExecutor is not null
                ? ResolutionProvenance.Explicit
                : ResolutionProvenance.NotApplicable,
        };
    }

    private static (bool, string) ResolveDecomposable(TypeConfig type, int childCount)
    {
        if (type.Decomposable.HasValue)
        {
            return (type.Decomposable.Value, ResolutionProvenance.Explicit);
        }
        if (childCount > 0)
        {
            return (true, ResolutionProvenance.Inferred);
        }
        if (type.AllowedChildTypes.Length > 0)
        {
            return (true, ResolutionProvenance.Inferred);
        }
        if (!string.IsNullOrWhiteSpace(type.DecompositionGuidance))
        {
            return (true, ResolutionProvenance.Inferred);
        }
        return (false, ResolutionProvenance.Inferred);
    }
}

/// <summary>Provenance tag for a resolved input — <c>explicit</c> means the
/// configuration declared it directly; <c>inferred</c> means the resolver
/// derived it from heuristics; <c>not_applicable</c> means the input is not
/// relevant for this item's facet set.</summary>
public static class ResolutionProvenance
{
    public const string Explicit = "explicit";
    public const string Inferred = "inferred";
    public const string NotApplicable = "not_applicable";
}

/// <summary>Resolved inputs ready to feed to <see cref="RequirementSetDeriver.Derive"/>,
/// with per-input provenance for transparency.</summary>
public sealed record ResolvedRequirementInputs
{
    public required bool Decomposable { get; init; }
    public required string DecomposableProvenance { get; init; }
    public string[]? FacetOrder { get; init; }
    public required string FacetOrderProvenance { get; init; }
    public string? ActionableExecutor { get; init; }
    public required string ActionableExecutorProvenance { get; init; }

    /// <summary>True when any input was resolved by inference rather than explicit config.</summary>
    public bool AnyInferred =>
        DecomposableProvenance == ResolutionProvenance.Inferred
        || FacetOrderProvenance == ResolutionProvenance.Inferred
        || ActionableExecutorProvenance == ResolutionProvenance.Inferred;
}
