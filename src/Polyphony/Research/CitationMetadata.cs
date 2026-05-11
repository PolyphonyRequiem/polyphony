namespace Polyphony.Research;

/// <summary>
/// Citation metadata attached to every kept artifact when it is promoted to
/// the sibling research repo. Satisfies the parent Issue's acceptance
/// criterion: "source URL, capture date, and a freshness signal."
/// </summary>
public sealed record CitationMetadata
{
    /// <summary>Original URL the artifact was fetched from.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>UTC ISO-8601 timestamp when the artifact was captured.</summary>
    public required string CaptureDate { get; init; }

    /// <summary>
    /// Coarse staleness band relative to <see cref="CaptureDate"/>.
    /// Values: <c>"fresh"</c> (≤ 24 h), <c>"recent"</c> (≤ 7 d),
    /// <c>"stale"</c> (&gt; 7 d). Forward-compatible with the citation
    /// rigor pass in #3077.
    /// </summary>
    public required string Freshness { get; init; }

    /// <summary>
    /// Computes the freshness band for a given capture time relative to
    /// <paramref name="now"/>.
    /// </summary>
    public static string ComputeFreshness(DateTimeOffset captureTime, DateTimeOffset now)
    {
        var age = now - captureTime;
        return age.TotalHours switch
        {
            <= 24 => "fresh",
            <= 168 => "recent", // 7 days
            _ => "stale",
        };
    }
}
