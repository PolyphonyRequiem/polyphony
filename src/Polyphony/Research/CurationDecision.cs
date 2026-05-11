namespace Polyphony.Research;

/// <summary>
/// String constants for archivist curation decisions. Used instead of an enum
/// so the JSON wire format is a plain string — no converter needed, and
/// downstream workflow routing can branch on the literal value.
/// </summary>
public static class CurationDecision
{
    /// <summary>
    /// Artifact is relevant and should be promoted to the sibling research repo.
    /// </summary>
    public const string Keep = "keep";

    /// <summary>
    /// Artifact is not relevant; scratch copy is silently pruned at apex close.
    /// </summary>
    public const string Discard = "discard";

    /// <summary>
    /// Artifact is promising but needs deeper research before promotion.
    /// Consumed by #3076 (loop-back).
    /// </summary>
    public const string Expand = "expand";

    /// <summary>
    /// Returns true when <paramref name="value"/> is one of the three
    /// recognised decision strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Keep or Discard or Expand;
}
