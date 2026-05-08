namespace Polyphony.Postconditions;

/// <summary>
/// Outcome of <see cref="IPostconditionVerifier.VerifyAsync"/>. Discriminated
/// union — every consumer handles the three cases explicitly so
/// "I forgot the conflict path" can't be a Class B bug.
///
/// <para>The verifier owns the "is the post-condition met?" decision; the
/// caller owns the response (no-op vs. push vs. force-push vs. escalate).
/// Callers MUST exhaustively switch on this type — adding a new case here
/// is a breaking change to all consumers, which is the whole point.</para>
///
/// <para>Three cases, mutually exclusive:</para>
/// <list type="bullet">
///   <item><see cref="Satisfied"/> — origin already holds every requested
///     blob at the expected content. Callers should no-op.</item>
///   <item><see cref="NeedsPush"/> — origin is missing one or more of the
///     requested paths. The caller already has the right content at HEAD
///     (since it's what they passed as expected) and a plain
///     <c>git push</c> will satisfy the post-condition.</item>
///   <item><see cref="Conflict"/> — origin holds the path but with content
///     different from <see cref="PostconditionExpectation.ExpectedContent"/>.
///     A plain push will be rejected as non-fast-forward. The caller must
///     decide whether to force-push, abort, or escalate.</item>
/// </list>
/// </summary>
public abstract record PostconditionOutcome
{
    /// <summary>
    /// Origin already holds every requested blob at the expected content.
    /// </summary>
    public sealed record Satisfied : PostconditionOutcome;

    /// <summary>
    /// Origin is missing one or more of the requested paths (either the
    /// remote branch itself doesn't exist, or the path doesn't exist at
    /// that ref). <see cref="Paths"/> lists the paths the caller still
    /// needs to push.
    /// </summary>
    public sealed record NeedsPush(IReadOnlyList<string> Paths) : PostconditionOutcome;

    /// <summary>
    /// Origin holds the path but with content different from what the
    /// caller declared expected. A plain <c>git push</c> will fail
    /// non-fast-forward; the caller decides whether to force-push,
    /// abort, or escalate.
    /// </summary>
    public sealed record Conflict(IReadOnlyList<PostconditionConflict> Conflicts) : PostconditionOutcome;
}

/// <summary>
/// Per-path mismatch detail surfaced by <see cref="PostconditionOutcome.Conflict"/>.
/// Carries enough information for the caller's diagnostic message;
/// content fields are intentionally raw strings so the caller can
/// truncate/diff them as it sees fit.
/// </summary>
public sealed record PostconditionConflict(
    string Path,
    string ExpectedContent,
    string ActualContent);
