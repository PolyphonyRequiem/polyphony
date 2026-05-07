using System.Text.Json.Serialization;

namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Result of an Azure DevOps auth probe (see
/// <see cref="IAdoClient.GetAuthStatusAsync"/>).
///
/// <para>
/// Mirrors the shape of <see cref="Polyphony.Infrastructure.Processes.GhAuthStatus"/>:
/// best-effort, never throws. Probe failures (missing PAT, 401/403, timeout,
/// network error) all surface as <see cref="IsAuthenticated"/> = <c>false</c>
/// with a human-readable <see cref="Detail"/> so preflight can route to
/// remediation rather than crash.
/// </para>
/// </summary>
/// <param name="IsAuthenticated">
/// True when the configured PAT (or token) successfully resolved against the
/// ADO connection-data endpoint.
/// </param>
/// <param name="Detail">
/// Human-readable detail describing the probe outcome — used in CLI output and
/// preflight diagnostics. Never null; empty when the probe succeeded with no
/// extra context to report.
/// </param>
/// <param name="OrganizationName">
/// The ADO organization the probe was run against (or the authenticated
/// user's display name when the org is not explicitly scoped). Null when the
/// probe failed before contacting ADO or the response did not carry it.
/// </param>
public sealed record AdoAuthStatus(
    bool IsAuthenticated,
    string Detail,
    string? OrganizationName);

/// <summary>
/// DTO for the <c>connectionData</c> ADO REST response. Used by
/// <see cref="AdoClient.GetAuthStatusAsync"/> to extract the authenticated
/// user's display name.
///
/// <para>
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/> for source-gen
/// deserialization. ADO returns camelCase property names, so each member uses
/// <see cref="JsonPropertyNameAttribute"/> to override the context's
/// snake_case_lower naming policy.
/// </para>
/// </summary>
public sealed class AdoConnectionData
{
    /// <summary>The authenticated identity, when the PAT resolved successfully.</summary>
    [JsonPropertyName("authenticatedUser")]
    public AdoConnectionDataUser? AuthenticatedUser { get; set; }
}

/// <summary>
/// User node nested inside <see cref="AdoConnectionData"/>.
/// Only the display name is read; other fields are ignored.
/// </summary>
public sealed class AdoConnectionDataUser
{
    /// <summary>
    /// Display name surfaced by the identity provider (typically the user's
    /// AAD display name). Falls back to <c>customDisplayName</c> when the
    /// provider value is missing.
    /// </summary>
    [JsonPropertyName("providerDisplayName")]
    public string? ProviderDisplayName { get; set; }

    /// <summary>Custom override for the display name; rarely populated.</summary>
    [JsonPropertyName("customDisplayName")]
    public string? CustomDisplayName { get; set; }
}

/// <summary>
/// Status filter accepted by <see cref="IAdoClient.ListPullRequestsAsync"/>.
/// Lower-cased name is sent as the <c>searchCriteria.status</c> query value.
/// </summary>
public enum AdoPullRequestStatus
{
    Active,
    Completed,
    Abandoned,
    All,
}

/// <summary>
/// Pull request projection returned by <see cref="IAdoClient"/> verbs.
///
/// <para>
/// Mapped from the raw ADO REST shape (<see cref="AdoPullRequestRaw"/>); the
/// raw <c>createdBy</c> object is flattened to its display name and the
/// canonical web URL is preferred over the API URL so output is human-usable.
/// </para>
/// </summary>
/// <param name="PullRequestId">ADO numeric PR identifier (unique within the repo).</param>
/// <param name="Title">PR title.</param>
/// <param name="Description">PR description (Markdown). Empty when unset.</param>
/// <param name="SourceRefName">Full source ref (e.g. <c>refs/heads/feature/x</c>).</param>
/// <param name="TargetRefName">Full target ref (e.g. <c>refs/heads/main</c>).</param>
/// <param name="Status">PR lifecycle state: <c>active</c>, <c>completed</c>, or <c>abandoned</c>.</param>
/// <param name="MergeStatus">
/// Last computed merge status: <c>succeeded</c>, <c>conflicts</c>, <c>queued</c>,
/// <c>rejectedByPolicy</c>, or <c>null</c> when ADO has not yet computed it.
/// </param>
/// <param name="CreatedBy">Display name of the PR author.</param>
/// <param name="CreationDate">UTC timestamp the PR was created.</param>
/// <param name="Url">Canonical web URL for the PR (the user-facing dev.azure.com page).</param>
public sealed record AdoPullRequest(
    int PullRequestId,
    string Title,
    string Description,
    string SourceRefName,
    string TargetRefName,
    string Status,
    string? MergeStatus,
    string CreatedBy,
    DateTime CreationDate,
    string Url);

/// <summary>
/// Wire-level body for <c>POST /_apis/git/repositories/{repo}/pullrequests</c>.
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/>.
/// </summary>
public sealed class AdoCreatePullRequestRequest
{
    [JsonPropertyName("sourceRefName")]
    public string SourceRefName { get; set; } = "";

    [JsonPropertyName("targetRefName")]
    public string TargetRefName { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// Wire-level body for
/// <c>PATCH /_apis/git/repositories/{repo}/pullRequests/{pr}/reviewers/{reviewerId}</c>.
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/>. Only the
/// <c>vote</c> field is sent — ADO ignores other fields on this verb when
/// the reviewer already exists.
/// </summary>
public sealed class AdoSetReviewerVoteRequest
{
    /// <summary>
    /// ADO reviewer vote enum: <c>10</c> approved, <c>5</c> approved with
    /// suggestions, <c>0</c> reset, <c>-5</c> waiting for author, <c>-10</c>
    /// rejected.
    /// </summary>
    [JsonPropertyName("vote")]
    public int Vote { get; set; }
}

/// <summary>
/// Wire-level body for the ADO "complete pull request" PATCH:
/// <c>PATCH /_apis/git/repositories/{repo}/pullRequests/{pr}</c>
/// with body
/// <c>{ status: "completed", lastMergeSourceCommit: { commitId }, completionOptions: { ... } }</c>.
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/>.
/// </summary>
/// <remarks>
/// Per ADR Rev 4 the merge strategy is pinned to <c>noFastForward</c> (a real
/// merge commit, never a fast-forward), matching the GitHub-side
/// <c>gh pr merge --merge</c>. <c>lastMergeSourceCommit.commitId</c> is the
/// stale-head guard — when the source branch has advanced past the supplied
/// SHA, ADO refuses with HTTP 409, which the verb routes as
/// <c>stale_head</c>.
/// </remarks>
public sealed class AdoCompletePullRequestRequest
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "completed";

    [JsonPropertyName("lastMergeSourceCommit")]
    public AdoCommitRef? LastMergeSourceCommit { get; set; }

    [JsonPropertyName("completionOptions")]
    public AdoCompletionOptions? CompletionOptions { get; set; }
}

/// <summary>
/// Nested <c>completionOptions</c> object inside
/// <see cref="AdoCompletePullRequestRequest"/>. Only the three fields the
/// merge-plan-ado verb cares about are surfaced — others (squashMerge,
/// transitionWorkItems, …) inherit ADO's defaults.
/// </summary>
public sealed class AdoCompletionOptions
{
    /// <summary>
    /// ADO merge strategy. Pinned to <c>noFastForward</c> for plan PRs
    /// (preserves the merge commit so sibling plan branches can still be
    /// reasoned about by SHA). Other accepted values per the ADO contract:
    /// <c>squash</c>, <c>rebase</c>, <c>rebaseMerge</c>.
    /// </summary>
    [JsonPropertyName("mergeStrategy")]
    public string MergeStrategy { get; set; } = "noFastForward";

    /// <summary>
    /// True ⇒ ADO deletes the source branch as part of the completion. Plan
    /// PRs leave it false because sibling plan branches may still be in flight.
    /// </summary>
    [JsonPropertyName("deleteSourceBranch")]
    public bool DeleteSourceBranch { get; set; }

    /// <summary>
    /// True ⇒ ADO bypasses branch-protection policies. Pinned to false in v1
    /// — the task spec defers a CLI-exposed bypass flag.
    /// </summary>
    [JsonPropertyName("bypassPolicy")]
    public bool BypassPolicy { get; set; }
}

/// <summary>
/// Outcome of <see cref="IAdoClient.CompletePullRequestAsync"/>. The call
/// has six observable shapes; rather than throw on the routable ones, the
/// verb consumes a structured projection so error-code mapping stays in one
/// place. AOT-safe: registered in <see cref="PolyphonyJsonContext"/>.
/// </summary>
/// <param name="Status">
/// Discriminator: <c>"completed"</c> (success), <c>"stale_head"</c>
/// (HTTP 409 — source branch advanced past the supplied SHA),
/// <c>"not_found"</c> (HTTP 404 — PR or repo missing),
/// <c>"not_mergeable"</c> (HTTP 400/409 — ADO refused for a non-stale
/// reason, e.g. policy block or active conflicts), or <c>"ado_error"</c>
/// (any other non-success status).
/// </param>
/// <param name="MergeCommitSha">
/// SHA of the merge commit ADO recorded. Populated only when
/// <see cref="Status"/> is <c>"completed"</c>; null otherwise.
/// </param>
/// <param name="HttpStatus">
/// Raw HTTP status returned by ADO. Populated for non-success outcomes so
/// the verb can include it in the error envelope.
/// </param>
/// <param name="ErrorBody">
/// Truncated response body for non-success outcomes (best-effort; may be
/// null when the body could not be read).
/// </param>
public sealed record AdoCompletePullRequestResult(
    string Status,
    string? MergeCommitSha,
    int? HttpStatus,
    string? ErrorBody);

/// <summary>
/// Wire-level envelope for the ADO PR list response (<c>{ "value": [...] }</c>).
/// </summary>
public sealed class AdoPullRequestListResponse
{
    [JsonPropertyName("value")]
    public AdoPullRequestRaw[]? Value { get; set; }
}

/// <summary>
/// Raw ADO PR shape. Mapped to the public <see cref="AdoPullRequest"/> record
/// inside <see cref="AdoClient"/>; not exposed on <see cref="IAdoClient"/>.
/// </summary>
public sealed class AdoPullRequestRaw
{
    [JsonPropertyName("pullRequestId")]
    public int PullRequestId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sourceRefName")]
    public string? SourceRefName { get; set; }

    [JsonPropertyName("targetRefName")]
    public string? TargetRefName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("mergeStatus")]
    public string? MergeStatus { get; set; }

    [JsonPropertyName("createdBy")]
    public AdoIdentityRef? CreatedBy { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTime CreationDate { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("_links")]
    public AdoReferenceLinks? Links { get; set; }
}

/// <summary>Minimal identity reference used by ADO REST DTOs (we only read the display name).</summary>
public sealed class AdoIdentityRef
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>Subset of the ADO <c>_links</c> envelope; only the <c>web</c> entry is consumed.</summary>
public sealed class AdoReferenceLinks
{
    [JsonPropertyName("web")]
    public AdoLink? Web { get; set; }
}

/// <summary>Single hyperlink entry inside <see cref="AdoReferenceLinks"/>.</summary>
public sealed class AdoLink
{
    [JsonPropertyName("href")]
    public string? Href { get; set; }
}

/// <summary>
/// Platform-neutral PR poll data for ADO PRs. Mirrors the shape of
/// <see cref="Polyphony.Infrastructure.Processes.GhPullRequestPollData"/> so
/// the polyphony <c>pr poll-status</c>/<c>pr poll-status-ado</c> verbs emit an
/// identical JSON envelope regardless of platform.
///
/// <para>
/// Composed from two ADO REST calls (<c>pullrequests/{id}</c> and
/// <c>pullrequests/{id}/reviewers</c>) — see
/// <see cref="IAdoClient.GetPullRequestPollDataAsync"/>. State and vote
/// vocabularies are pre-normalized so consumers never need to reason about
/// raw ADO ints or status strings.
/// </para>
/// </summary>
public sealed record AdoPullRequestPollData
{
    /// <summary>ADO numeric PR identifier (unique within the repo).</summary>
    public required int Number { get; init; }

    /// <summary>"OPEN" | "MERGED" | "CLOSED" — platform-neutral. Mapped from ADO <c>status</c>.</summary>
    public required string State { get; init; }

    /// <summary>"APPROVED" | "REVIEW_REQUIRED" | "REJECTED" — aggregated from reviewer votes.</summary>
    public required string ReviewDecision { get; init; }

    /// <summary>"MERGEABLE" | "CONFLICTING" | "UNKNOWN" — mapped from ADO <c>mergeStatus</c>.</summary>
    public required string Mergeable { get; init; }

    /// <summary>Source branch short name (e.g. <c>feature/x</c>); <c>refs/heads/</c> stripped.</summary>
    public required string HeadRefName { get; init; }

    /// <summary>Current head SHA on the source branch (from <c>lastMergeSourceCommit.commitId</c>); empty when ADO omits it.</summary>
    public required string HeadRefOid { get; init; }

    /// <summary>Target branch short name (e.g. <c>main</c>); <c>refs/heads/</c> stripped.</summary>
    public required string BaseRefName { get; init; }

    /// <summary>UTC timestamp the PR was merged; null when not merged.</summary>
    public DateTime? MergedAt { get; init; }

    /// <summary>Merge commit SHA when state is MERGED; null otherwise.</summary>
    public string? MergeCommit { get; init; }

    /// <summary>PR description (Markdown). Empty when ADO returned null.</summary>
    public required string Body { get; init; }

    /// <summary>All reviewers on the PR, ordered as ADO returned them.</summary>
    public required IReadOnlyList<AdoPullRequestReview> Reviews { get; init; }
}

/// <summary>
/// Single reviewer entry returned by ADO's <c>/reviewers</c> endpoint,
/// projected to the platform-neutral vote vocabulary.
/// </summary>
public sealed record AdoPullRequestReview
{
    /// <summary>Reviewer display name (or unique name when display name is missing).</summary>
    public required string Identity { get; init; }

    /// <summary>
    /// Normalized vote — <c>approved</c> (10), <c>approved_with_suggestions</c> (5),
    /// <c>no_vote</c> (0), <c>waiting_for_author</c> (-5), or <c>rejected</c> (-10).
    /// </summary>
    public required string Vote { get; init; }

    /// <summary>
    /// Always null for ADO — the reviewers endpoint does not surface a per-vote
    /// timestamp. Field exists to keep the JSON envelope identical across platforms.
    /// </summary>
    public DateTime? SubmittedAt { get; init; }
}

/// <summary>
/// Wire-level shape of the ADO single-PR detail endpoint
/// (<c>GET /_apis/git/repositories/{repo}/pullrequests/{id}</c>). Carries the
/// extra fields beyond <see cref="AdoPullRequestRaw"/> that
/// <see cref="IAdoClient.GetPullRequestPollDataAsync"/> needs:
/// <c>lastMergeSourceCommit</c> (for HeadRefOid), <c>lastMergeCommit</c>
/// (for MergeCommit), and <c>closedDate</c> (for MergedAt). Mapped to
/// <see cref="AdoPullRequestPollData"/> inside <see cref="AdoClient"/>.
/// </summary>
public sealed class AdoPullRequestDetailRaw
{
    [JsonPropertyName("pullRequestId")]
    public int PullRequestId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sourceRefName")]
    public string? SourceRefName { get; set; }

    [JsonPropertyName("targetRefName")]
    public string? TargetRefName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("mergeStatus")]
    public string? MergeStatus { get; set; }

    [JsonPropertyName("closedDate")]
    public DateTime? ClosedDate { get; set; }

    [JsonPropertyName("lastMergeSourceCommit")]
    public AdoCommitRef? LastMergeSourceCommit { get; set; }

    [JsonPropertyName("lastMergeCommit")]
    public AdoCommitRef? LastMergeCommit { get; set; }
}

/// <summary>
/// Minimal commit reference used in <see cref="AdoPullRequestDetailRaw"/> —
/// only <c>commitId</c> is consumed.
/// </summary>
public sealed class AdoCommitRef
{
    [JsonPropertyName("commitId")]
    public string? CommitId { get; set; }
}

/// <summary>
/// Wire-level envelope for ADO's PR reviewers list response
/// (<c>{ "count": N, "value": [...] }</c>).
/// </summary>
public sealed class AdoReviewerListResponse
{
    [JsonPropertyName("value")]
    public AdoReviewerRaw[]? Value { get; set; }
}

/// <summary>
/// Wire-level shape for a single entry in the ADO reviewers list. Only the
/// fields needed for vote normalization + decision aggregation are read.
/// </summary>
public sealed class AdoReviewerRaw
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }

    /// <summary>
    /// ADO's reviewer vote enum: <c>10</c> approved, <c>5</c> approved with
    /// suggestions, <c>0</c> no vote, <c>-5</c> waiting for author, <c>-10</c>
    /// rejected.
    /// </summary>
    [JsonPropertyName("vote")]
    public int Vote { get; set; }

    /// <summary>True when the reviewer is required (counts toward the APPROVED aggregation).</summary>
    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }
}

/// <summary>
/// Known ADO <c>mergeStatus</c> string values, normalized to the
/// platform-neutral mergeable vocabulary by
/// <see cref="AdoMergeStatus.Map"/>. Exists to keep the magic strings in one
/// place rather than scattered through <see cref="AdoClient"/>.
/// </summary>
internal static class AdoMergeStatus
{
    internal const string Succeeded = "succeeded";
    internal const string Conflicts = "conflicts";

    /// <summary>
    /// Map an ADO <c>mergeStatus</c> string to the platform-neutral mergeable
    /// vocabulary: <c>"MERGEABLE"</c>, <c>"CONFLICTING"</c>, or
    /// <c>"UNKNOWN"</c> (for <c>queued</c>, <c>notSet</c>, <c>rejectedByPolicy</c>,
    /// null, or any value ADO adds in the future).
    /// </summary>
    internal static string Map(string? mergeStatus) => mergeStatus switch
    {
        Succeeded => "MERGEABLE",
        Conflicts => "CONFLICTING",
        _ => "UNKNOWN",
    };
}

/// <summary>
/// Wire-level body for
/// <c>POST /_apis/git/repositories/{repo}/pullRequests/{pr}/threads</c>.
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/>.
///
/// <para>
/// A "comment thread" in ADO bundles one or more comments with a thread-level
/// <c>status</c>. The advisory <c>post-comment-ado</c> verb posts a single
/// top-level comment in a closed thread (status: 4) — see
/// <see cref="AdoCreateThreadComment"/> for the per-comment fields.
/// </para>
/// </summary>
public sealed class AdoCreateThreadRequest
{
    /// <summary>
    /// Comments inside this thread. The advisory verb sends a single entry
    /// (one top-level text comment); ADO permits multiple but we don't use
    /// that shape.
    /// </summary>
    [JsonPropertyName("comments")]
    public List<AdoCreateThreadComment> Comments { get; set; } = new();

    /// <summary>
    /// Thread-level status (ADO <c>CommentThreadStatus</c> enum):
    /// <c>1</c> active, <c>2</c> fixed, <c>3</c> wontFix, <c>4</c> closed,
    /// <c>5</c> byDesign, <c>6</c> pending. The advisory verb pins this to
    /// <c>4</c> (closed) — no follow-up reply is expected.
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; } = 4;
}

/// <summary>
/// Single comment entry inside <see cref="AdoCreateThreadRequest"/>.
/// </summary>
public sealed class AdoCreateThreadComment
{
    /// <summary>
    /// Parent comment ID for replies; <c>0</c> for a top-level comment in
    /// a fresh thread.
    /// </summary>
    [JsonPropertyName("parentCommentId")]
    public int ParentCommentId { get; set; }

    /// <summary>The comment body (Markdown when commentType: 1).</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>
    /// ADO <c>CommentType</c> enum: <c>1</c> text, <c>2</c> codeChange,
    /// <c>3</c> system. Pinned to <c>1</c> for advisory comments.
    /// </summary>
    [JsonPropertyName("commentType")]
    public int CommentType { get; set; } = 1;
}

/// <summary>
/// Wire-level shape of the ADO <c>POST .../threads</c> response. Only the
/// thread ID and the inner comments' IDs are read; everything else is
/// ignored. AOT-safe: registered in <see cref="PolyphonyJsonContext"/>.
/// </summary>
public sealed class AdoCreateThreadResponse
{
    /// <summary>The thread ID assigned by ADO.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>The created comments inside the thread (we expect exactly one).</summary>
    [JsonPropertyName("comments")]
    public List<AdoCreateThreadResponseComment>? Comments { get; set; }
}

/// <summary>
/// Single comment entry inside <see cref="AdoCreateThreadResponse"/>; only
/// the ID is read.
/// </summary>
public sealed class AdoCreateThreadResponseComment
{
    /// <summary>The comment ID assigned by ADO.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

/// <summary>
/// Public DTO returned to the verb by
/// <see cref="IAdoClient.CreatePullRequestCommentThreadAsync"/> on success.
/// Carries the IDs the verb echoes into its JSON envelope so callers can
/// link to the posted comment later.
/// </summary>
/// <param name="ThreadId">Thread ID assigned by ADO.</param>
/// <param name="CommentId">ID of the (single) top-level comment created inside the thread.</param>
public sealed record AdoCreateThreadResult(int ThreadId, int CommentId);

// ───────────────────────────────────────────────────────────────────────────
// List PR comment threads — wire-level + public projection.
// Used by IAdoClient.ListPullRequestThreadsAsync, which the
// `pr get-comments-ado` verb consumes to harvest review comment text.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wire-level envelope for the ADO PR threads list response
/// (<c>{ "count": N, "value": [...] }</c>). AOT-safe: registered in
/// <see cref="PolyphonyJsonContext"/>.
/// </summary>
public sealed class AdoThreadListResponse
{
    [JsonPropertyName("value")]
    public AdoThreadRaw[]? Value { get; set; }
}

/// <summary>
/// Wire-level shape of a single ADO PR comment thread. Only the fields
/// consumed by <see cref="IAdoClient.ListPullRequestThreadsAsync"/> are
/// modelled; ADO's response carries many more (identities, properties,
/// pullRequestThreadContext.iterationContext) that we ignore today.
/// </summary>
public sealed class AdoThreadRaw
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// ADO <c>CommentThreadStatus</c> enum value, returned as a string by
    /// the GET endpoint (<c>active | pending | fixed | wontFix | closed |
    /// byDesign | unknown</c>). Note ADO accepts an int on POST but only
    /// returns the string form on GET.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    [JsonPropertyName("publishedDate")]
    public DateTime? PublishedDate { get; set; }

    [JsonPropertyName("lastUpdatedDate")]
    public DateTime? LastUpdatedDate { get; set; }

    [JsonPropertyName("comments")]
    public List<AdoCommentRaw>? Comments { get; set; }

    [JsonPropertyName("threadContext")]
    public AdoThreadContextRaw? ThreadContext { get; set; }
}

/// <summary>
/// Wire-level shape of a single comment inside <see cref="AdoThreadRaw"/>.
/// Only the fields consumed by <see cref="IAdoClient.ListPullRequestThreadsAsync"/>
/// are modelled.
/// </summary>
public sealed class AdoCommentRaw
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("parentCommentId")]
    public int ParentCommentId { get; set; }

    [JsonPropertyName("author")]
    public AdoCommentAuthor? Author { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("publishedDate")]
    public DateTime? PublishedDate { get; set; }

    [JsonPropertyName("lastUpdatedDate")]
    public DateTime? LastUpdatedDate { get; set; }

    /// <summary>
    /// ADO <c>CommentType</c> enum value, returned as a string by the GET
    /// endpoint (<c>text | codeChange | system | unknown</c>).
    /// </summary>
    [JsonPropertyName("commentType")]
    public string? CommentType { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Wire-level shape of <c>thread.author</c>. Different from
/// <see cref="AdoIdentityRef"/> in that we want to fall back to the unique
/// name (typically email) when the display name is missing — useful when
/// the comment author is a service principal.
/// </summary>
public sealed class AdoCommentAuthor
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }
}

/// <summary>
/// Wire-level shape of a thread's code anchor. Threads with no
/// <c>threadContext</c> are top-level PR comments (no file/line position).
/// We only consume <c>filePath</c> and the right-side line numbers; the
/// left-side fields exist for callers that want to flag deletion-only
/// comments but the verb does not use them today.
/// </summary>
public sealed class AdoThreadContextRaw
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("rightFileStart")]
    public AdoFilePositionRaw? RightFileStart { get; set; }

    [JsonPropertyName("rightFileEnd")]
    public AdoFilePositionRaw? RightFileEnd { get; set; }
}

/// <summary>
/// Single line/offset position inside <see cref="AdoThreadContextRaw"/>.
/// </summary>
public sealed class AdoFilePositionRaw
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

/// <summary>
/// Public projection of an ADO PR comment thread returned by
/// <see cref="IAdoClient.ListPullRequestThreadsAsync"/>. The verb consumes
/// this and flattens it into the per-comment rows of
/// <see cref="PrGetCommentsAdoResult.Comments"/>; tests assert on the
/// projection shape (rather than the raw wire shape) so the contract is
/// stable across ADO API revisions.
///
/// <para>The <c>isDeleted</c> threads and <c>system</c>/<c>isDeleted</c>
/// comments are filtered out before this projection is composed — callers
/// only see human-authored, non-tombstoned content.</para>
/// </summary>
public sealed record AdoPullRequestThread
{
    /// <summary>Thread ID assigned by ADO; unique within the PR.</summary>
    public required int Id { get; init; }

    /// <summary>
    /// Thread status (lowercased ADO <c>CommentThreadStatus</c> name):
    /// <c>active | pending | fixed | wontFix | closed | byDesign | unknown</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>True when the thread's status is <c>fixed</c>, <c>wontFix</c>, <c>closed</c>, or <c>byDesign</c>.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>File path the thread is anchored to; null for top-level PR comments.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based line on the right (post-change) side; null when the thread has no right-side anchor.</summary>
    public int? Line { get; init; }

    /// <summary>Comments inside the thread (oldest first), human-authored only.</summary>
    public required IReadOnlyList<AdoPullRequestComment> Comments { get; init; }
}

/// <summary>
/// Public projection of a single comment inside an ADO PR thread. Mirrors
/// the per-comment fields the verb echoes into
/// <see cref="AdoPrComment"/> after flattening.
/// </summary>
public sealed record AdoPullRequestComment
{
    /// <summary>Comment ID (unique within its parent thread).</summary>
    public required int Id { get; init; }

    /// <summary>Parent comment ID; <c>0</c> for the top-level comment in a thread.</summary>
    public required int ParentCommentId { get; init; }

    /// <summary>Author display name (falls back to unique name); empty when ADO surfaced neither.</summary>
    public required string Author { get; init; }

    /// <summary>Comment body (Markdown). Empty when ADO returned null content.</summary>
    public required string Body { get; init; }

    /// <summary>UTC timestamp the comment was first published.</summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>UTC timestamp of the most recent edit; equals <see cref="PublishedAt"/> when never edited.</summary>
    public DateTime? LastUpdatedAt { get; init; }

    /// <summary>Comment type (lowercased ADO <c>CommentType</c> name): typically <c>text</c>.</summary>
    public required string CommentType { get; init; }
}
