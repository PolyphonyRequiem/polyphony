namespace Polyphony.Sdlc;

/// <summary>
/// Resolved per-item guidance configuration after <c>policy.yaml</c> layering.
/// The <see cref="Source"/> string is one of the <see cref="GuidanceSource"/>
/// constants. <see cref="AdoFieldName"/> is non-null only when
/// <see cref="Source"/> is <see cref="GuidanceSource.AdoField"/>.
/// </summary>
/// <remarks>
/// Produced by <see cref="Polyphony.Policy.PolicyResolver.ResolveGuidance"/>
/// and consumed by <see cref="Polyphony.Guidance.GuidanceExtractor.Extract"/>.
/// </remarks>
public sealed record GuidanceConfig(
    string Source,
    string? AdoFieldName);
