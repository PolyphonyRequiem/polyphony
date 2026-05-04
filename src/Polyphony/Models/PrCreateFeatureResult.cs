namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr create-feature-pr</c>: feature PR ID/URL plus
/// metadata. Mirrors the JSON contract of <c>scripts/feature-pr-creator.ps1</c>.
/// </summary>
public sealed record PrCreateFeatureResult
{
    public required int PrNumber { get; init; }
    public required string PrUrl { get; init; }
    public required string Title { get; init; }
    public required string DescriptionSummary { get; init; }

    /// <summary>True when a new PR was opened; false when an existing open PR was reused.</summary>
    public required bool Created { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
