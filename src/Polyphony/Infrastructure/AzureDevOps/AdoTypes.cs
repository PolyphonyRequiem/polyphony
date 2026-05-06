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
