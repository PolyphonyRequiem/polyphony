namespace Polyphony.Tagging;

/// <summary>
/// Discriminated union of all tags the polyphony pipeline owns on ADO work
/// items. Organised into three variant families:
///
/// <list type="bullet">
///   <item><b>State</b> — reflect pipeline lifecycle state (e.g. planned).</item>
///   <item><b>Intent</b> — declare structural role (in-scope, root).</item>
///   <item><b>Facet</b> — per-item facet overrides (carry data).</item>
/// </list>
///
/// The <see cref="Reset"/> verb uses this union as the sole source of truth
/// for which tags to strip when scrubbing a root. Downstream consumers
/// (e.g. scope verbs, requirement deriver) can also use it to classify tags
/// without repeating string constants.
///
/// <para>The union is intentionally closed: the private constructor prevents
/// external types from inheriting, so a <c>switch</c> over the sealed cases
/// is exhaustive.</para>
/// </summary>
public abstract record PolyphonyTag
{
    private PolyphonyTag() { }

    // ─── State variants ──────────────────────────────────────────────────

    /// <summary>
    /// <c>polyphony:planned</c> — set by the planner on plan completion.
    /// </summary>
    public sealed record Planned : PolyphonyTag;

    // ─── Intent variants ─────────────────────────────────────────────────

    /// <summary>
    /// <c>polyphony</c> — bare in-scope marker for pipeline descendants.
    /// </summary>
    public sealed record InScope : PolyphonyTag;

    /// <summary>
    /// <c>polyphony:root</c> — marks an item as the run's apex.
    /// </summary>
    public sealed record Root : PolyphonyTag;

    // ─── Facet variants ──────────────────────────────────────────────────

    /// <summary>
    /// <c>polyphony:facets=csv</c> — per-item facet override.
    /// </summary>
    public sealed record Facets(IReadOnlyList<string> FacetNames) : PolyphonyTag;

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a <see cref="PolyphonyTag"/> back to its ADO tag string form.
    /// </summary>
    public static string ToTagString(PolyphonyTag tag) => tag switch
    {
        Planned => PolyphonyTags.Planned,
        InScope => PolyphonyTags.InScope,
        Root => PolyphonyTags.Root,
        Facets f => $"{PolyphonyTags.FacetsPrefix}={string.Join(',', f.FacetNames)}",
        _ => throw new InvalidOperationException($"Unhandled PolyphonyTag variant: {tag.GetType().Name}")
    };

    /// <summary>
    /// Attempts to parse a raw ADO tag string into a <see cref="PolyphonyTag"/>.
    /// Returns null for tags outside the polyphony namespace.
    /// </summary>
    public static PolyphonyTag? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();

        if (string.Equals(trimmed, PolyphonyTags.InScope, StringComparison.OrdinalIgnoreCase))
            return new InScope();

        if (string.Equals(trimmed, PolyphonyTags.Root, StringComparison.OrdinalIgnoreCase))
            return new Root();

        if (string.Equals(trimmed, PolyphonyTags.Planned, StringComparison.OrdinalIgnoreCase))
            return new Planned();

        var facetsPrefix = PolyphonyTags.FacetsPrefix + "=";
        if (trimmed.StartsWith(facetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var csv = trimmed[facetsPrefix.Length..];
            var names = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new Facets(names);
        }

        return null;
    }

    /// <summary>
    /// Enumerates every polyphony-owned tag present in a <see cref="TagSet"/>.
    /// Tags outside the polyphony namespace are silently skipped.
    /// </summary>
    public static IReadOnlyList<PolyphonyTag> AllOwned(TagSet tags)
    {
        var result = new List<PolyphonyTag>();
        foreach (var raw in tags)
        {
            if (TryParse(raw) is { } parsed)
                result.Add(parsed);
        }
        return result;
    }

    /// <summary>
    /// Returns true if the raw tag string belongs to the polyphony namespace
    /// (i.e. equals "polyphony" or starts with "polyphony:"). Used by reset
    /// to catch any polyphony-owned tag including future variants that may
    /// not yet have a DU case.
    /// </summary>
    public static bool IsPolyphonyOwned(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        return string.Equals(trimmed, PolyphonyTags.InScope, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("polyphony:", StringComparison.OrdinalIgnoreCase);
    }
}
