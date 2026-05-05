namespace Polyphony;

/// <summary>
/// JSON contract for <c>polyphony scope check</c>. See <c>docs/polyphony-tags.md</c>.
/// </summary>
public sealed record ScopeCheckResult
{
    public int WorkItemId { get; init; }
    public bool InScope { get; init; }
    public bool IsRoot { get; init; }
    public required string[] Tags { get; init; }
    public string? Error { get; init; }
}
