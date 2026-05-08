using Polyphony.Configuration;

namespace Polyphony.Sdlc;

/// <summary>
/// Resolves the per-item inputs the <see cref="RequirementSetDeriver"/> needs
/// (<c>decomposable</c>, <c>facet_order</c>, <c>actionable_executor</c>,
/// <c>execution_mode</c>) from the <see cref="TypeConfig"/> + observable
/// signals (existing children, etc.) when the configuration does not declare
/// them explicitly.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>stop-gap inference layer</b> for Phase 2b. The planner-declared
/// per-instance authority intended by the glossary lives in Phase 7's worklist
/// work; until then, this resolver chooses sensible defaults and surfaces
/// provenance (<see cref="ResolutionProvenance.Explicit"/> vs
/// <see cref="ResolutionProvenance.Inferred"/> vs
/// <see cref="ResolutionProvenance.Default"/>) so consumers know how a value
/// was arrived at.
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
/// <para>
/// <c>execution_mode</c> is taken from <see cref="TypeConfig.ExecutionMode"/>
/// when set to a known value (<see cref="ResolutionProvenance.Explicit"/>),
/// otherwise defaults to <see cref="Sdlc.ExecutionMode.Parallel"/>
/// (<see cref="ResolutionProvenance.Default"/>). Validation of unknown values
/// lives at config-load time (<c>ConfigValidator</c> rule V-19), not here.
/// </para>
/// </remarks>
public static class RequirementInputResolver
{
    /// <summary>Resolve inputs for an item.</summary>
    /// <param name="type">The type-config to consult for defaults.</param>
    /// <param name="childCount">Observed number of children (used for the
    /// decomposable inference fallback).</param>
    /// <param name="overrideFacets">Optional per-item facet override sourced
    /// from the item's <c>polyphony:facets=...</c> tag (closed-loop PR #7).
    /// When non-null and non-empty, REPLACES <see cref="TypeConfig.Facets"/>
    /// for this call only — the type-config object is not mutated. When null
    /// or empty, the resolver falls back to <see cref="TypeConfig.Facets"/>.
    /// Tokens are not re-validated here; callers MUST pass values already
    /// vetted by <see cref="FacetTagParser"/>.</param>
    public static ResolvedRequirementInputs Resolve(
        TypeConfig type,
        int childCount,
        IReadOnlyList<string>? overrideFacets = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        var (decomposable, decomposableProvenance) = ResolveDecomposable(type, childCount);
        var (executionMode, executionModeProvenance) = ResolveExecutionMode(type);
        var (facets, facetsProvenance) = ResolveFacets(type, overrideFacets);
        return new ResolvedRequirementInputs
        {
            Facets = facets,
            FacetsProvenance = facetsProvenance,
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
            ExecutionMode = executionMode,
            ExecutionModeProvenance = executionModeProvenance,
        };
    }

    private static (IReadOnlyList<string>, string) ResolveFacets(
        TypeConfig type,
        IReadOnlyList<string>? overrideFacets)
    {
        if (overrideFacets is { Count: > 0 })
        {
            // Override is per-call — type.Facets is left untouched. The
            // override carries Explicit provenance: the architect declared
            // it in plan front-matter, which is a stronger signal than the
            // type-config default.
            return (overrideFacets, ResolutionProvenance.Explicit);
        }
        // Fall back to the type-config default. Provenance mirrors whether
        // the config declares any facets at all — an empty default is itself
        // a deliberate "pure container" choice and surfaces as Default.
        var configured = type.Facets ?? [];
        return configured.Length > 0
            ? (configured, ResolutionProvenance.Explicit)
            : ((IReadOnlyList<string>)configured, ResolutionProvenance.Default);
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

    private static (string, string) ResolveExecutionMode(TypeConfig type)
    {
        // Empty/whitespace is treated as "unset" — fall back to default.
        // Validation of unknown non-empty values lives at config-load time
        // (ConfigValidator V-19), not here; if the value somehow reaches
        // the resolver, we still pass it through with Explicit provenance
        // so downstream consumers see the planner's intent.
        if (string.IsNullOrWhiteSpace(type.ExecutionMode))
        {
            return (ExecutionMode.Parallel, ResolutionProvenance.Default);
        }
        return (type.ExecutionMode, ResolutionProvenance.Explicit);
    }
}

/// <summary>Provenance tag for a resolved input — <c>explicit</c> means the
/// configuration declared it directly; <c>inferred</c> means the resolver
/// derived it from heuristics; <c>default</c> means the configuration did not
/// declare a value and the resolver returned the documented default;
/// <c>not_applicable</c> means the input is not relevant for this item's
/// facet set.</summary>
public static class ResolutionProvenance
{
    public const string Explicit = "explicit";
    public const string Inferred = "inferred";

    /// <summary>The resolver returned the documented default for this input
    /// because the configuration did not specify a value. Distinct from
    /// <see cref="Inferred"/> (which uses heuristics over observable signals)
    /// — there is no inference here, only a static fallback.</summary>
    public const string Default = "default";

    public const string NotApplicable = "not_applicable";
}

/// <summary>Resolved inputs ready to feed to <see cref="RequirementSetDeriver.Derive"/>,
/// with per-input provenance for transparency.</summary>
public sealed record ResolvedRequirementInputs
{
    /// <summary>The facet set the deriver should consume — either the
    /// per-item override (architect-declared via plan front-matter and
    /// surfaced as the <c>polyphony:facets=...</c> tag) or the type-config
    /// default. Always non-null; may be empty for pure containers.</summary>
    public IReadOnlyList<string> Facets { get; init; } = [];

    /// <summary>Provenance for <see cref="Facets"/> —
    /// <see cref="ResolutionProvenance.Explicit"/> when the override was
    /// supplied OR the type-config declared a non-empty list,
    /// <see cref="ResolutionProvenance.Default"/> when the type-config
    /// declared no facets at all.</summary>
    public string FacetsProvenance { get; init; } = ResolutionProvenance.Default;

    public required bool Decomposable { get; init; }
    public required string DecomposableProvenance { get; init; }
    public string[]? FacetOrder { get; init; }
    public required string FacetOrderProvenance { get; init; }
    public string? ActionableExecutor { get; init; }
    public required string ActionableExecutorProvenance { get; init; }

    /// <summary>The execution mode that PR #5's edge injection will consume.
    /// Always populated; defaults to <see cref="Sdlc.ExecutionMode.Parallel"/>
    /// when the config does not declare one.</summary>
    public required string ExecutionMode { get; init; }

    /// <summary>Provenance for <see cref="ExecutionMode"/> —
    /// <see cref="ResolutionProvenance.Explicit"/> when the config supplied a
    /// value, <see cref="ResolutionProvenance.Default"/> when the resolver
    /// fell back to <see cref="Sdlc.ExecutionMode.Parallel"/>.</summary>
    public required string ExecutionModeProvenance { get; init; }

    /// <summary>True when any input was resolved by inference rather than explicit config.</summary>
    public bool AnyInferred =>
        DecomposableProvenance == ResolutionProvenance.Inferred
        || FacetOrderProvenance == ResolutionProvenance.Inferred
        || ActionableExecutorProvenance == ResolutionProvenance.Inferred
        || FacetsProvenance == ResolutionProvenance.Inferred;
}
