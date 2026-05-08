namespace Polyphony;

/// <summary>
/// Single reviewer entry in <see cref="PrPollStatusResult.Reviewers"/>.
/// Platform-neutral by design — same shape regardless of whether the
/// underlying source was GitHub or Azure DevOps. The vote vocabulary
/// is normalized:
/// <list type="bullet">
///   <item><c>approved</c> — the reviewer signed off (GitHub APPROVED, ADO 10/5, magic comment <c>polyphony:approve</c>).</item>
///   <item><c>changes_requested</c> — explicit block (GitHub CHANGES_REQUESTED, ADO -10/-5, magic comment <c>polyphony:request-changes</c>).</item>
///   <item><c>commented</c> — comment-only review (GitHub COMMENTED).</item>
///   <item><c>dismissed</c> — review was dismissed/superseded (GitHub DISMISSED).</item>
///   <item><c>pending</c> — review requested but not yet submitted.</item>
/// </list>
/// </summary>
public sealed record PrPollReviewer
{
    /// <summary>Reviewer identity — GitHub login or ADO display name. May be empty for bot reviews when the underlying source omits the field.</summary>
    public required string Identity { get; init; }

    /// <summary>Normalized vote — see record summary for the vocabulary.</summary>
    public required string Vote { get; init; }

    /// <summary>ISO-8601 timestamp of when the review was submitted; null when the source omits it (e.g. pending).</summary>
    public string? SubmittedAt { get; init; }

    /// <summary>
    /// Where the vote came from. <c>review</c> (default) — a native platform
    /// review (GitHub Reviews tab, ADO vote). <c>magic_comment</c> — a
    /// PR-author-posted top-level comment matching <c>polyphony:approve</c>
    /// or <c>polyphony:request-changes</c>, used to work around GitHub's
    /// PR-author-cannot-self-approve restriction. See issue #207 for the
    /// design rationale and the planned replacement options.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>
/// Policy block — captures the verb's local opinion on whether the PR
/// can be merged right now. Composed of source-platform signals
/// (review decision + mergeability) plus blocking reasons. The verb
/// does NOT consult external policy (e.g. <c>polyphony policy resolve</c>);
/// it returns enough context for the consumer to apply policy.
/// </summary>
public sealed record PrPollPolicy
{
    /// <summary>True when the PR is approved AND mergeable AND not stale.</summary>
    public required bool MergeAllowed { get; init; }

    /// <summary>Human-readable reasons merging is currently disallowed. Empty when MergeAllowed is true.</summary>
    public required IReadOnlyList<string> BlockingReasons { get; init; }
}

/// <summary>
/// Plan-PR front-matter parsed out of the PR body when
/// <c>--include-metadata</c> is set. Two well-known keys:
/// <list type="bullet">
///   <item><c>requests_parent_change</c>: bool — child plan PR is requesting a change to its parent's plan.</item>
///   <item><c>ancestor_plan_generations</c>: map of ancestor item id → generation snapshot at branch creation. Used by the cascade rule.</item>
/// </list>
/// Always populated when <c>--include-metadata</c> is set; missing keys
/// default to <c>requests_parent_change: false</c> and an empty
/// <c>ancestor_plan_generations</c> map. Omitted entirely when the flag
/// is not set, so the verb can safely poll impl/MG PRs that don't carry
/// front-matter.
/// </summary>
public sealed record PrPollMetadata
{
    /// <summary>Default false. True when the plan PR carries a request to amend its parent plan.</summary>
    public required bool RequestsParentChange { get; init; }

    /// <summary>Map of ancestor item id (or <c>"root"</c>) to <c>plan_generation</c> snapshot recorded at branch creation. Empty for non-plan PRs.</summary>
    public required IReadOnlyDictionary<string, int> AncestorPlanGenerations { get; init; }
}

/// <summary>
/// Output of <c>polyphony pr poll-status</c>. Platform-neutral
/// aggregated PR status — the workflow's single source of truth for
/// "where is this PR right now?" routing decisions. Same schema for
/// GitHub and (eventually) Azure DevOps.
///
/// <para>State vocabulary (non-overlapping):</para>
/// <list type="bullet">
///   <item><c>merged</c> — PR has been merged.</item>
///   <item><c>closed</c> — PR is closed without merging.</item>
///   <item><c>approved</c> — PR is open and the platform-aggregated decision is APPROVED.</item>
///   <item><c>changes_requested</c> — PR is open and at least one reviewer requested changes (or the platform's aggregated decision says CHANGES_REQUESTED).</item>
///   <item><c>pending</c> — PR is open with no decision yet (REVIEW_REQUIRED, no reviews, etc.).</item>
///   <item><c>error</c> — only set when <see cref="Error"/> is populated.</item>
/// </list>
/// </summary>
public sealed record PrPollStatusResult
{
    /// <summary>The PR URL the verb was asked to poll (echoed back for traceability).</summary>
    public required string PrUrl { get; init; }

    /// <summary>The PR number parsed out of the URL.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The repo slug parsed out of the URL (e.g. <c>polyphonyrequiem/polyphony</c>).</summary>
    public required string RepoSlug { get; init; }

    /// <summary>Normalized status — see type summary for vocabulary.</summary>
    public required string State { get; init; }

    /// <summary>Current SHA of the head ref. May be empty when gh omits it.</summary>
    public required string HeadSha { get; init; }

    /// <summary>Source branch name.</summary>
    public required string HeadRef { get; init; }

    /// <summary>Target branch name.</summary>
    public required string BaseRef { get; init; }

    /// <summary>True when mergeable, false when conflicting, null when the source can't tell yet.</summary>
    public bool? Mergeable { get; init; }

    /// <summary>Merge commit SHA when <see cref="State"/> is <c>merged</c>; null otherwise.</summary>
    public string? MergeCommitSha { get; init; }

    /// <summary>ISO-8601 timestamp when the PR was merged; null when not merged.</summary>
    public string? MergedAt { get; init; }

    /// <summary>All reviews on the PR, normalized and ordered as the source returned them.</summary>
    public required IReadOnlyList<PrPollReviewer> Reviewers { get; init; }

    /// <summary>Local policy opinion — informational; not authoritative.</summary>
    public required PrPollPolicy Policy { get; init; }

    /// <summary>Plan-PR front-matter when <c>--include-metadata</c> is set. Null otherwise.</summary>
    public PrPollMetadata? Metadata { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Machine-readable error code that pairs with <see cref="Error"/>. Used by
    /// the platform-specific verbs (e.g. <c>pr poll-status-ado</c>) to surface
    /// a stable enum the consuming workflow can branch on without parsing
    /// human-readable error text. Vocabulary is verb-defined; ADO uses
    /// <c>pr_not_found</c>, <c>ado_timeout</c>, <c>ado_failed</c>,
    /// <c>invalid_argument</c>, <c>no_pat</c>. Omitted on success and on
    /// errors where no stable code is available.
    /// </summary>
    public string? ErrorCode { get; init; }
}
