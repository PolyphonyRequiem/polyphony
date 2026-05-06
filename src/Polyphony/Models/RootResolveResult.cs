namespace Polyphony;

/// <summary>
/// JSON contract for <c>polyphony root resolve</c>. See <c>docs/polyphony-tags.md</c>.
/// <see cref="FallbackRequired"/> indicates the workflow should fire the root
/// fallback gate (no <c>polyphony:root</c> ancestor was found within the
/// configured walk budget).
/// </summary>
public sealed record RootResolveResult
{
    public int WorkItemId { get; init; }
    public int? ResolvedRootId { get; init; }
    public required IReadOnlyList<int> AncestorsWalked { get; init; }
    public bool FallbackRequired { get; init; }
    public string? Error { get; init; }
}
