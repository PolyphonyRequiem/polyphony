namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr check-evidence-floor &lt;pr-number&gt;</c>:
/// the mechanical pre-reviewer gate that catches "agent crashed before
/// producing anything" misfires (≥1 commit on the head branch beyond the
/// base AND a non-empty PR description after trim) before the LLM
/// reviewer is asked to judge an empty PR.
///
/// <para>Routing-style envelope — the verb always exits 0; consumers
/// branch on <see cref="PassesFloor"/> (the happy/sad split) or
/// <see cref="ErrorCode"/> (transport failures: <c>pr_not_found</c>,
/// <c>gh_failed</c>). The two are mutually exclusive: when
/// <see cref="ErrorCode"/> is non-null the floor was not evaluated and
/// <see cref="PassesFloor"/> is false.</para>
///
/// <para>Per the Phase 6 design sketch (pick #6), the floor is a STRICT
/// MECHANICAL bar and content judgment is the LLM reviewer's exclusive
/// province. Do not extend this verb with content-quality checks.</para>
/// </summary>
public sealed record PrCheckEvidenceFloorResult
{
    /// <summary>True when the verb completed and produced an answer
    /// (whether the floor passed or not). False only when an error
    /// envelope is populated (PR not found, gh failed). Mirrors the
    /// routing-style envelope contract used across the PR-verb family.</summary>
    public required bool Success { get; init; }

    /// <summary>The PR number the floor was evaluated against. Echoed
    /// verbatim so workflow consumers can correlate without re-deriving.</summary>
    public required int PrNumber { get; init; }

    /// <summary>Number of commits on the PR's head branch beyond its base,
    /// as reported by <c>gh pr view --json commits</c>. Zero when no
    /// commits exist OR when the verb failed before reading the PR
    /// (paired with <see cref="Success"/> = false).</summary>
    public required int CommitCount { get; init; }

    /// <summary>Length of the PR body AFTER whitespace trim. Zero when
    /// the body is empty / whitespace-only. Workflow templates can
    /// reference this for diagnostics without having to recompute trim
    /// semantics.</summary>
    public required int BodyLength { get; init; }

    /// <summary>True iff every floor rule passed (<see cref="CommitCount"/>
    /// meets the configured minimum AND <see cref="BodyLength"/> &gt; 0).
    /// False when any rule was violated OR the verb failed.</summary>
    public required bool PassesFloor { get; init; }

    /// <summary>Stable machine-readable rule names for every violation.
    /// Defined values: <c>no_commits</c>, <c>empty_body</c>. Empty when
    /// the floor passed OR when an error envelope is populated. Listed
    /// in declaration order (commits first, body second) so workflow
    /// templates can render them deterministically.</summary>
    public required IReadOnlyList<string> Violations { get; init; }

    /// <summary>Stable machine-readable error code on transport
    /// failure: <c>pr_not_found</c> when gh reported a 404 / unresolved
    /// PR, <c>gh_failed</c> for any other gh subprocess failure. Null on
    /// success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error detail (typically the gh stderr
    /// trimmed). Null on success.</summary>
    public string? ErrorMessage { get; init; }
}
