namespace Polyphony;

/// <summary>
/// Single reviewer entry in <see cref="PrPollStatusResult.Reviewers"/>.
/// Platform-neutral by design — same shape regardless of whether the
/// underlying source was GitHub or Azure DevOps. The vote vocabulary
/// is normalized:
/// <list type="bullet">
///   <item><c>approved</c> — the reviewer signed off (GitHub APPROVED, ADO 10/5, magic comment <c>polyphony:approve [sha]</c>).</item>
///   <item><c>changes_requested</c> — explicit block (GitHub CHANGES_REQUESTED, ADO -10/-5, author SHA-bound magic comment <c>polyphony:request-changes &lt;sha&gt;</c>). Non-author reviewers should still prefer review threads; the magic-comment form exists specifically for the GitHub PR-author self-block case (authors cannot REQUEST_CHANGES on their own PR).</item>
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
    /// Where the vote came from. Vocabulary:
    /// <list type="bullet">
    ///   <item><c>review</c> (default) — a native platform review (GitHub Reviews tab, ADO vote).</item>
    ///   <item><c>magic_comment_sha_bound</c> — a PR-author-posted top-level comment matching <c>polyphony:approve &lt;head-sha&gt;</c> where the SHA matches the PR's current head. Canonical fallback for GitHub's PR-author-cannot-self-approve restriction; the SHA pins the approval to a specific commit so any new push silently invalidates it.</item>
    ///   <item><c>magic_comment</c> — a PR-author-posted top-level comment matching the bare <c>polyphony:approve</c> form (no SHA). Recognized as a deprecation fallback; emits a warning recommending the SHA-bound form.</item>
    ///   <item><c>magic_comment_request_changes</c> — a PR-author-posted top-level comment matching <c>polyphony:request-changes &lt;head-sha&gt;</c> where the SHA matches the PR's current head. The author self-block path. SHA is mandatory — there is no bare-form fallback; the bare form was retired in option B because it was a permanent loop trigger and the SHA binding restores the structural self-invalidation property.</item>
    /// </list>
    /// See issue #207 for the design rationale.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>
/// Single review-thread entry in <see cref="PrPollStatusResult.Threads"/>.
/// Threads are the source of truth for the <c>changes_requested</c> gate
/// after this verb's "option B" rewrite (issue #207): an unresolved,
/// non-outdated review thread blocks merge regardless of any stale
/// native CHANGES_REQUESTED review the platform may still surface.
///
/// <para>Platform-neutral by design — same shape regardless of whether
/// the underlying source was GitHub (GraphQL <c>reviewThreads</c>) or
/// Azure DevOps (REST <c>/threads</c>).</para>
/// </summary>
public sealed record PrPollThread
{
    /// <summary>Stable thread identifier from the platform (GraphQL node id on GitHub; integer-as-string on ADO).</summary>
    public required string Id { get; init; }

    /// <summary>True when the thread is marked resolved (GitHub <c>isResolved</c>; ADO status in {fixed, wontFix, closed, byDesign}).</summary>
    public required bool IsResolved { get; init; }

    /// <summary>
    /// True when GitHub flags the thread as outdated (the anchored
    /// hunk has been rewritten beyond recognition). Always null on ADO
    /// — the platform doesn't expose an outdated bit. Outdated +
    /// unresolved threads do NOT block merge — see derivation rules
    /// in <see cref="PrPollStatusResult"/>.
    /// </summary>
    public bool? IsOutdated { get; init; }

    /// <summary>Raw platform status (e.g. GitHub: <c>"RESOLVED"|"UNRESOLVED"|"PENDING"</c>; ADO: <c>"active"|"fixed"|"wontFix"|...</c>). Surfaced for diagnostics; not used for routing.</summary>
    public required string Status { get; init; }

    /// <summary>Login/display-name of the first comment's author (the thread initiator). May be empty when the platform omits it.</summary>
    public string? AuthorIdentity { get; init; }

    /// <summary>ISO-8601 timestamp of the first comment in the thread; null when the platform omits it.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Number of human-authored comments in the thread (after platform-side system-comment filtering).</summary>
    public required int CommentCount { get; init; }

    /// <summary>
    /// Bodies of the human-authored comments inside this thread, oldest
    /// first. Always populated (may be empty when the upstream platform
    /// fetch failed to include them). Consumed by the
    /// <c>pr_feedback_analyzer</c> agent so it can judge whether an
    /// unresolved thread represents a blocking concern or a resolved-by-
    /// discussion non-issue.
    /// </summary>
    public required IReadOnlyList<PrPollComment> Comments { get; init; }
}

/// <summary>
/// Single comment inside a PR — either a top-level PR comment (when
/// listed in <see cref="PrPollStatusResult.Comments"/>) or a comment
/// inside a review thread (when listed in <see cref="PrPollThread.Comments"/>).
/// Platform-neutral; bodies are raw markdown as posted by the author.
///
/// <para>The <see cref="Marker"/> field is populated when the body
/// contains an HTML comment of the form
/// <c>&lt;!-- polyphony:agent-comment agent=X head_sha=Y run_id=Z --&gt;</c>.
/// Polyphony's automated posters (e.g. <c>plan_reviewer_poster_ado</c>)
/// inject this marker so downstream analyzers can reliably identify
/// machine-authored comments regardless of which operator token posted
/// them. See <c>PrCommentMarker</c>.</para>
/// </summary>
public sealed record PrPollComment
{
    /// <summary>Comment author identity (GitHub login, ADO display name, or unique name). Empty when the platform omitted it.</summary>
    public required string Author { get; init; }

    /// <summary>Comment body — raw markdown as posted. May contain the bot-identity marker; see <see cref="Marker"/>.</summary>
    public required string Body { get; init; }

    /// <summary>ISO-8601 timestamp of when the comment was posted; null when the platform omitted it.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>
    /// Parsed agent-identity marker when the body starts with a
    /// recognized <c>&lt;!-- polyphony:agent-comment ... --&gt;</c>
    /// HTML comment. Null when the body has no marker (the common
    /// case for human-authored comments). Consumers use this to
    /// distinguish bot feedback from human feedback even when both
    /// were posted via the same operator token.
    /// </summary>
    public PrPollCommentMarker? Marker { get; init; }
}

/// <summary>
/// Parsed contents of a <c>&lt;!-- polyphony:agent-comment ... --&gt;</c>
/// HTML marker on the first line of a PR comment body. Polyphony's
/// automated posters inject this marker; the
/// <c>pr_feedback_analyzer</c> reads it to identify bot comments
/// reliably even when bot and human use the same auth token.
/// </summary>
public sealed record PrPollCommentMarker
{
    /// <summary>The conductor agent that authored the comment (e.g. <c>plan_reviewer</c>, <c>feature_pr_updater</c>). Required — the marker is invalid without it.</summary>
    public required string Agent { get; init; }

    /// <summary>The PR head SHA at the time the marker was posted, when the poster captured it. Null when the poster did not provide one.</summary>
    public string? HeadSha { get; init; }

    /// <summary>The conductor run identifier that produced the comment, when the poster captured it. Null when the poster did not provide one.</summary>
    public string? RunId { get; init; }
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
/// GitHub and Azure DevOps.
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
///
/// <para><b>State derivation order ("option B", issue #207):</b></para>
/// <list type="number">
///   <item>If platform state is <c>MERGED</c> or <c>CLOSED</c>, short-circuit to the matching value.</item>
///   <item>If <see cref="Threads"/> contains any thread that is <c>!IsResolved &amp;&amp; IsOutdated != true</c>, return <c>changes_requested</c>. <b>Threads dominate</b>: an unresolved thread blocks merge regardless of any APPROVED native review or APPROVED aggregated decision.</item>
///   <item>If <see cref="Threads"/> is non-empty (all resolved, or the only unresolved threads are outdated), the platform's stale CHANGES_REQUESTED native review is <b>suppressed</b>: positive approval requires either an APPROVED native review or a magic-comment fallback. Otherwise <c>pending</c>.</item>
///   <item>If <see cref="Threads"/> is empty, fall back to the platform's aggregated review decision (APPROVED → <c>approved</c>; CHANGES_REQUESTED → <c>changes_requested</c>).</item>
///   <item>If neither threads nor a native decision give an answer, fall back to a deprecated magic-comment scan (<c>polyphony:approve</c> from the PR author only — <c>polyphony:request-changes</c> is no longer recognized; resolve a thread instead). When this path fires, a deprecation warning is appended to <see cref="Warnings"/>.</item>
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

    /// <summary>
    /// Deterministic terminal-route classifier output for the
    /// sentiment-driven PR review loop. One of:
    /// <list type="bullet">
    ///   <item><c>merge_now</c> — all signals positive, no unresolved actionable threads; workflow should invoke the merger.</item>
    ///   <item><c>already_merged</c> — PR is already merged on the platform; workflow should skip the merger.</item>
    ///   <item><c>abort_unmerged</c> — PR is closed/abandoned, or any reviewer cast a rejecting vote (ADO -10); workflow should bail this leg.</item>
    ///   <item><c>none</c> — mixed/partial signals; workflow should route to the LLM <c>pr_feedback_analyzer</c> agent to interpret comments.</item>
    /// </list>
    /// Computed by <see cref="PrPollTerminalRoute.Classify"/>. Empty on
    /// the error envelope path. Workflow conditions should switch on
    /// this field; <see cref="State"/> is retained for back-compat with
    /// pre-sentiment workflows that have not yet been migrated.
    /// </summary>
    public required string Route { get; init; }

    /// <summary>
    /// Short human-readable explanation of how <see cref="Route"/> was
    /// derived. For operator logs / dashboard display only — never
    /// branch on this string.
    /// </summary>
    public required string RouteReason { get; init; }

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

    /// <summary>
    /// All review threads on the PR, normalized to a platform-neutral
    /// shape. Empty (not null) when the PR has no review threads OR when
    /// the verb could not fetch them (in which case <see cref="Warnings"/>
    /// is populated with a "thread fetch failed" message).
    /// See <see cref="PrPollStatusResult"/> docstring for how threads
    /// drive state derivation.
    /// </summary>
    public required IReadOnlyList<PrPollThread> Threads { get; init; }

    /// <summary>
    /// Top-level PR comments (issue comments on GitHub; top-level
    /// thread-zero comments on ADO). Empty (not null) when the PR has
    /// none. Review (inline diff) comments are NOT included here — those
    /// live inside <see cref="PrPollThread.Comments"/>. Consumed by the
    /// <c>pr_feedback_analyzer</c> agent.
    /// </summary>
    public required IReadOnlyList<PrPollComment> Comments { get; init; }

    /// <summary>
    /// PR author's stable identity (GitHub login on the GitHub leg;
    /// ADO unique name on the ADO leg). Empty when the upstream platform
    /// omitted it. Consumed by the <c>pr_feedback_analyzer</c> to
    /// exclude self-comments from the negative-feedback calculation
    /// (PR authors explain their own work; that's not a request for
    /// changes against themselves).
    /// </summary>
    public required string AuthorIdentity { get; init; }

    /// <summary>Local policy opinion — informational; not authoritative.</summary>
    public required PrPollPolicy Policy { get; init; }

    /// <summary>
    /// Non-fatal advisories surfaced by the verb (deprecation notices,
    /// pagination cutoffs, missing-platform-feature fallbacks). Null when
    /// no warnings were generated. Consumers should log but not branch on
    /// these — they are diagnostic, not routing signals.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

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
