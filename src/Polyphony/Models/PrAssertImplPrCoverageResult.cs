namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr assert-impl-pr-coverage</c>: verifies that
/// the squash-merge of an impl PR onto the MG branch carries the full
/// cumulative diff of the source impl branch. Defends against AB#3211 —
/// silent commit drop on squash merge, observed in apex 3165 where
/// <c>mg/3165_pg-3176</c> received only 1 of 3 commits' worth of diff
/// from <c>impl/3165-3176</c>.
///
/// The verb is read-only; it does not push, merge, or mutate refs.
/// </summary>
public sealed record PrAssertImplPrCoverageResult
{
    /// <summary>
    /// Verdict: <c>ok</c> (squash carried the full impl-branch diff),
    /// <c>mismatch</c> (the squash diff differs from the cumulative
    /// impl-branch diff — silent commit drop suspected), or <c>error</c>
    /// (input validation or git invocation failed).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>The root work-item id supplied as input.</summary>
    public required int RootId { get; init; }

    /// <summary>The per-item work-item id supplied as input.</summary>
    public required int ItemId { get; init; }

    /// <summary>The MG path supplied as input (e.g. <c>pg-3176</c>).</summary>
    public required string MgPath { get; init; }

    /// <summary>
    /// Fully-qualified impl branch ref the verb compared against
    /// (e.g. <c>origin/impl/3165-3176</c>).
    /// </summary>
    public required string ImplRef { get; init; }

    /// <summary>
    /// Fully-qualified MG branch ref the verb compared against
    /// (e.g. <c>origin/mg/3165_pg-3176</c>).
    /// </summary>
    public required string MgRef { get; init; }

    /// <summary>
    /// SHA of the merge base used as the comparison anchor (the parent
    /// commit on the MG branch — equivalent to <c>{mg_ref}^</c>).
    /// </summary>
    public required string ComparisonBase { get; init; }

    /// <summary>
    /// Hex-encoded SHA-256 hash of the cumulative diff of the impl
    /// branch from <see cref="ComparisonBase"/> (i.e. what the squash
    /// SHOULD have carried).
    /// </summary>
    public required string ExpectedDiffHash { get; init; }

    /// <summary>
    /// Hex-encoded SHA-256 hash of the actual diff carried by the
    /// MG-branch squash commit (i.e. what the squash ACTUALLY carried).
    /// </summary>
    public required string ActualDiffHash { get; init; }

    /// <summary>
    /// Total bytes in the expected (cumulative impl-branch) diff text.
    /// Surfaced so the gate prompt can show operators the size delta
    /// even when both diffs hash identically (defensive zero-length
    /// case).
    /// </summary>
    public required int ExpectedDiffBytes { get; init; }

    /// <summary>Total bytes in the actual (squash-commit) diff text.</summary>
    public required int ActualDiffBytes { get; init; }

    /// <summary>
    /// Commits on the impl branch since the MG-branch parent, oldest
    /// first. Operators consult this list when <see cref="Action"/> is
    /// <c>mismatch</c> to identify which commits' diffs may have been
    /// dropped from the squash. Empty when the impl branch has zero
    /// commits ahead of the comparison base.
    /// </summary>
    public required IReadOnlyList<PrAssertImplPrCoverageCommit> ImplBranchCommits { get; init; }

    /// <summary>Non-empty when the operation failed (input validation or git error).</summary>
    public string? Error { get; init; }
}

/// <summary>
/// One commit on the impl branch as enumerated by
/// <see cref="PrAssertImplPrCoverageResult.ImplBranchCommits"/>.
/// </summary>
public sealed record PrAssertImplPrCoverageCommit
{
    /// <summary>Full commit SHA (40 hex chars).</summary>
    public required string Sha { get; init; }

    /// <summary>Commit subject line (first line of the commit message).</summary>
    public required string Subject { get; init; }
}
