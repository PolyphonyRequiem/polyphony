namespace Polyphony;

/// <summary>
/// JSON contract for <c>polyphony scope list</c>. See <c>docs/polyphony-tags.md</c>.
/// </summary>
public sealed record ScopeListResult
{
    public int RootId { get; init; }
    public required IReadOnlyList<ScopeListItem> InScopeItems { get; init; }
    public required IReadOnlyList<ScopeListItem> OutOfScopeItems { get; init; }
    public int InScopeCount { get; init; }
    public int OutOfScopeCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// One row in <see cref="ScopeListResult"/>. Tag content is intentionally
/// elided here — callers that want tags should use <c>polyphony scope check</c>.
/// </summary>
public sealed record ScopeListItem
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }
    public bool IsRoot { get; init; }
}
