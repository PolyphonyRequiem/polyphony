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
