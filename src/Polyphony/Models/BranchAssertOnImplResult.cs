namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch assert-on-impl</c>: confirms (or refutes)
/// that the current git HEAD is on the expected per-item impl branch
/// <c>impl/{root_id}-{item_id}</c>. Defends against AB#3210 — silent
/// commit misroute when the impl agent runs against a HEAD that does not
/// match the task it was dispatched for. The verb is read-only; it does
/// not check anything out, push, or mutate state.
/// </summary>
public sealed record BranchAssertOnImplResult
{
    /// <summary>
    /// Verdict: <c>ok</c> (HEAD matches expected branch), <c>mismatch</c>
    /// (HEAD is on a different branch, or detached), or <c>error</c>
    /// (git invocation failed, invalid input, etc.).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// The branch name HEAD is expected to be on
    /// (e.g. <c>impl/3165-3175</c>).
    /// </summary>
    public required string ExpectedBranch { get; init; }

    /// <summary>
    /// The branch name HEAD is actually on, or empty when HEAD is
    /// detached or could not be determined.
    /// </summary>
    public required string ActualBranch { get; init; }

    /// <summary>The root work-item id supplied as input.</summary>
    public required int RootId { get; init; }

    /// <summary>The non-root work-item id supplied as input.</summary>
    public required int ItemId { get; init; }

    /// <summary>Non-empty when the operation failed (input validation or git error).</summary>
    public string? Error { get; init; }
}
