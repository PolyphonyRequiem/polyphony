namespace Polyphony.Tagging;

/// <summary>
/// Constants for the <c>polyphony:*</c> tag namespace stamped on ADO work
/// items by the polyphony pipeline.
///
/// Authoritative spec: <c>docs/polyphony-tags.md</c>.
/// </summary>
public static class PolyphonyTags
{
    /// <summary>
    /// Bare scope-ownership tag. Indicates a descendant of a root that is
    /// in-scope for the polyphony pipeline.
    /// </summary>
    public const string InScope = "polyphony";

    /// <summary>
    /// Marks an item as a root for the polyphony pipeline. Implies in-scope.
    /// Used by <c>polyphony root resolve</c> to walk up to the nearest root.
    /// </summary>
    public const string Root = "polyphony:root";

    /// <summary>
    /// Status sub-tag set by the planner agent on plan completion. Pre-existing
    /// behaviour, preserved as-is. Read by the plan-level workflow's
    /// resume-detection gate.
    /// </summary>
    public const string Planned = "polyphony:planned";

    /// <summary>
    /// Tag-name prefix for the per-item facet override (closed-loop PR #7).
    /// Stamped by <c>polyphony plan seed-children</c> as
    /// <c>polyphony:facets=&lt;csv&gt;</c> when the architect declared
    /// <c>apex_facets</c> in plan front-matter for an indivisible apex.
    /// Read by <see cref="Sdlc.RequirementInputResolver"/> to override the
    /// type-config default facet set on a per-call basis. The full tag
    /// shape (prefix + <c>=</c> + canonical csv) is owned by
    /// <see cref="Sdlc.FacetTagParser"/>.
    /// </summary>
    public const string FacetsPrefix = "polyphony:facets";

    /// <summary>
    /// Returns true if the given parsed tag set indicates the item is in-scope
    /// (carries either <see cref="InScope"/> or <see cref="Root"/>).
    /// </summary>
    public static bool IsInScope(TagSet tags) =>
        tags.Contains(InScope) || tags.Contains(Root);

    /// <summary>
    /// Returns true if the given parsed tag set marks this item as a root.
    /// </summary>
    public static bool IsRoot(TagSet tags) => tags.Contains(Root);
}
