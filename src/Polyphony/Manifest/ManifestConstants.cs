namespace Polyphony.Manifest;

/// <summary>
/// Wire-string constants for the <c>nesting</c> field in
/// <see cref="MergeGroupEntry"/>. The values are normative per the Rev 4
/// branch-model ADR (<c>docs/decisions/branch-model.md</c>).
/// </summary>
public static class ManifestNesting
{
    /// <summary>A top-level merge group sitting directly under <c>feature/</c>.</summary>
    public const string Top = "top";

    /// <summary>A merge group nested under another merge group.</summary>
    public const string Nested = "nested";

    /// <summary>The set of valid wire values.</summary>
    public static readonly IReadOnlySet<string> ValidValues = new HashSet<string>(StringComparer.Ordinal)
    {
        Top, Nested,
    };
}

/// <summary>
/// Wire-string constants for the <c>isolation</c> field in
/// <see cref="MergeGroupEntry"/>. Hyphenated wire values per the Rev 4
/// branch-model ADR — these MUST round-trip exactly as written because
/// they participate in the topology hash as raw text.
/// </summary>
public static class ManifestIsolation
{
    /// <summary>One worktree per merge group; items inside the MG serialize.</summary>
    public const string PerMergeGroup = "per-merge-group";

    /// <summary>One worktree per implementable leaf; items run in parallel.</summary>
    public const string PerItem = "per-item";

    /// <summary>The set of valid wire values.</summary>
    public static readonly IReadOnlySet<string> ValidValues = new HashSet<string>(StringComparer.Ordinal)
    {
        PerMergeGroup, PerItem,
    };
}

/// <summary>
/// Wire-string constants for the <c>nesting_override</c> sentinel value
/// <c>"flat"</c>. The override field accepts <c>null</c> (no override),
/// the literal <c>"flat"</c> (force flat task PR), or any valid
/// <c>MergeGroupId</c> (name the nested MG explicitly). <c>"flat"</c> is
/// reserved and MUST NOT be used as an MG id even though it parses as a
/// valid MG-id grammar string.
/// </summary>
public static class ManifestOverride
{
    /// <summary>Reserved sentinel value forcing a flat task PR for the child.</summary>
    public const string Flat = "flat";
}
