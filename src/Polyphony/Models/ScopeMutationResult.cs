namespace Polyphony;

/// <summary>
/// JSON contract for <c>polyphony scope tag/untag</c> and
/// <c>polyphony root declare</c>. <see cref="Changed"/> is the idempotency
/// signal — false when the tag was already in the desired state and no ADO
/// write was performed.
/// </summary>
public sealed record ScopeMutationResult
{
    public int WorkItemId { get; init; }
    public bool Changed { get; init; }
    public required string[] TagsBefore { get; init; }
    public required string[] TagsAfter { get; init; }
    public string? Error { get; init; }
}
