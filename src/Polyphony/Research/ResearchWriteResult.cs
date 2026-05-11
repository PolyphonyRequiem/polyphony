namespace Polyphony.Research;

/// <summary>
/// Outcome of a single file write through <see cref="IResearchStore"/>.
/// Carries enough context for the promotion verb to report meaningful
/// results and safely retry.
/// </summary>
public sealed record ResearchWriteResult
{
    /// <summary>
    /// Outcome of the write: <c>"created"</c>, <c>"updated"</c>,
    /// <c>"no_op"</c> (content unchanged), or <c>"failed"</c>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Path that was written (relative to the repo root).</summary>
    public required string Path { get; init; }

    /// <summary>Error detail when <see cref="Outcome"/> is <c>"failed"</c>.</summary>
    public string? Error { get; init; }

    public static class Outcomes
    {
        public const string Created = "created";
        public const string Updated = "updated";
        public const string NoOp = "no_op";
        public const string Failed = "failed";
    }
}
