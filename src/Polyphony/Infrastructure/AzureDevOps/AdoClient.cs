using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps.Auth;

namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Default <see cref="IAdoClient"/> backed by <see cref="HttpClient"/>.
///
/// <para>
/// Every HTTP call flows through <see cref="SendWithRetryAsync"/>, which
/// applies the configured <see cref="AdoClientPolicy"/>: per-attempt
/// timeout (linked CTS cancels the in-flight request when the timeout
/// fires), retry-on-timeout only, and exponential backoff between retries.
/// Caller-driven cancellation propagates immediately and is never converted
/// into a retryable timeout. HTTP status codes (including 4xx/5xx) are
/// returned to the caller without retry — a 401 will not become a 200 on
/// the next attempt.
/// </para>
///
/// <para>
/// Authentication is delegated to <see cref="IPolyphonyAuthProvider"/>,
/// which returns a complete <c>Authorization</c> header value: either
/// <c>"Basic base64(:pat)"</c> for env-var PAT, or a raw JWT (transmitted
/// as <c>"Bearer …"</c>) for the AAD MSAL chain. The provider is consulted
/// per-request so a rotated PAT or refreshed token is picked up on the next
/// call without restart.
/// </para>
/// </summary>
public sealed class AdoClient : IAdoClient
{
    /// <summary>
    /// The probe endpoint. Org-scoped variants exist
    /// (<c>https://dev.azure.com/{org}/_apis/connectionData</c>) but the
    /// org-less form returns 200 for any valid PAT regardless of which
    /// org(s) the PAT was issued for, which is exactly what the auth probe
    /// wants to verify.
    /// </summary>
    internal const string ConnectionDataUrl = "https://dev.azure.com/_apis/connectionData?api-version=7.1";

    private readonly HttpClient _http;
    private readonly IPolyphonyAuthProvider _authProvider;
    private readonly AdoClientPolicy _policy;

    /// <summary>Production constructor; uses <see cref="AdoClientPolicy.Default"/>.</summary>
    public AdoClient(HttpClient http, IPolyphonyAuthProvider authProvider)
        : this(http, authProvider, AdoClientPolicy.Default)
    {
    }

    /// <summary>Test constructor accepting an explicit policy.</summary>
    public AdoClient(HttpClient http, IPolyphonyAuthProvider authProvider, AdoClientPolicy policy)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public async Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
    {
        string header;
        try
        {
            header = await _authProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
        }
        catch (AdoAuthenticationException ex)
        {
            return new AdoAuthStatus(
                IsAuthenticated: false,
                Detail: ex.Message,
                OrganizationName: null);
        }

        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildProbeRequest(header), ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new AdoAuthStatus(
                    IsAuthenticated: false,
                    Detail: "Credentials rejected",
                    OrganizationName: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AdoAuthStatus(
                    IsAuthenticated: false,
                    Detail: $"Probe failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    OrganizationName: null);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new AdoAuthStatus(
                    IsAuthenticated: false,
                    Detail: $"Probe failed: could not read response body ({ex.Message})",
                    OrganizationName: null);
            }

            AdoConnectionData? data;
            try
            {
                data = JsonSerializer.Deserialize(body, PolyphonyJsonContext.Default.AdoConnectionData);
            }
            catch (JsonException ex)
            {
                return new AdoAuthStatus(
                    IsAuthenticated: false,
                    Detail: $"Probe failed: malformed JSON ({ex.Message})",
                    OrganizationName: null);
            }

            // 200 with body: credentials are valid. Surface the display name when present so
            // CLI output can confirm "logged in as <name>" rather than just "OK".
            var displayName = data?.AuthenticatedUser?.ProviderDisplayName
                ?? data?.AuthenticatedUser?.CustomDisplayName;
            return new AdoAuthStatus(
                IsAuthenticated: true,
                Detail: string.IsNullOrEmpty(displayName)
                    ? "Authenticated"
                    : $"Authenticated as {displayName}",
                OrganizationName: displayName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate so verbs can honour shutdown signals.
            throw;
        }
        catch (TimeoutException)
        {
            // Per-attempt timeout exhausted by SendWithRetryAsync.
            return new AdoAuthStatus(
                IsAuthenticated: false,
                Detail: "Timed out probing dev.azure.com",
                OrganizationName: null);
        }
        catch (HttpRequestException ex)
        {
            return new AdoAuthStatus(
                IsAuthenticated: false,
                Detail: $"Probe failed: {ex.Message}",
                OrganizationName: null);
        }
        catch (Exception ex)
        {
            return new AdoAuthStatus(
                IsAuthenticated: false,
                Detail: $"Probe failed: {ex.Message}",
                OrganizationName: null);
        }
    }

    private static HttpRequestMessage BuildProbeRequest(string authHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ConnectionDataUrl);
        AddAuthHeaders(request, authHeader);
        return request;
    }

    /// <summary>
    /// Apply the resolved Authorization header value plus a JSON Accept
    /// header to the request. The header value's shape determines the
    /// scheme:
    /// <list type="bullet">
    /// <item><c>"Basic …"</c> — set verbatim (PAT-as-password form).</item>
    /// <item>anything else — treated as a raw bearer token (JWT) and
    ///       wrapped as <c>"Bearer …"</c>.</item>
    /// </list>
    /// </summary>
    private static void AddAuthHeaders(HttpRequestMessage request, string authHeader)
    {
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // Set verbatim — the value is already the complete header.
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }
        else
        {
            // Raw token — apply Bearer scheme.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeader);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Resolve the Authorization header or throw. Used by operational verbs
    /// (PR list/get/create) that have no graceful "unauthenticated"
    /// projection — unlike the auth probe, which encodes "no credentials"
    /// as a status outcome.
    /// </summary>
    private Task<string> ResolveAuthHeaderOrThrowAsync(CancellationToken ct)
        => _authProvider.GetAccessTokenAsync(ct);

    /// <summary>
    /// Map the wire-level <see cref="AdoPullRequestRaw"/> shape to the public
    /// <see cref="AdoPullRequest"/> projection. The <c>Url</c> field is
    /// always synthesised as the canonical web URL
    /// <c>https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}</c>
    /// from the request context — ADO's <c>raw.url</c> is the API URL
    /// (and <c>_links.web.href</c> is not always populated on POST
    /// responses), so neither is suitable for human-facing display.
    /// </summary>
    private static AdoPullRequest MapPullRequest(
        AdoPullRequestRaw raw,
        string organization,
        string project,
        string repository)
    {
        return new AdoPullRequest(
            PullRequestId: raw.PullRequestId,
            Title: raw.Title ?? "",
            Description: raw.Description ?? "",
            SourceRefName: raw.SourceRefName ?? "",
            TargetRefName: raw.TargetRefName ?? "",
            Status: raw.Status ?? "",
            MergeStatus: raw.MergeStatus,
            CreatedBy: raw.CreatedBy?.DisplayName ?? "",
            CreationDate: raw.CreationDate,
            Url: BuildCanonicalPrUrl(organization, project, repository, raw.PullRequestId));
    }

    /// <summary>
    /// Synthesise the canonical ADO PR web URL
    /// (<c>https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}</c>).
    /// Returns an empty string when any component is missing, matching the
    /// shared convention in <see cref="Polyphony.Commands.PrCommands"/>.
    /// </summary>
    internal static string BuildCanonicalPrUrl(
        string organization,
        string project,
        string repository,
        int pullRequestId)
    {
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            return string.Empty;
        }
        return $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
               $"/_git/{Uri.EscapeDataString(repository)}/pullrequest/{pullRequestId}";
    }

    /// <summary>
    /// Normalize a branch identifier to the full ref form ADO requires.
    /// Pass-through when the value already starts with <c>refs/</c>.
    /// </summary>
    internal static string NormalizeBranchRef(string branch)
    {
        if (string.IsNullOrEmpty(branch)) return branch;
        return branch.StartsWith("refs/", StringComparison.Ordinal)
            ? branch
            : "refs/heads/" + branch;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
        string organization,
        string project,
        string repository,
        AdoPullRequestStatus status = AdoPullRequestStatus.Active,
        string? sourceBranch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullrequests" +
                  $"?searchCriteria.status={StatusToQueryValue(status)}";
        if (!string.IsNullOrEmpty(sourceBranch))
        {
            // ADO requires the full ref form (refs/heads/...) on the
            // sourceRefName filter — short branch names silently match
            // nothing.
            var sourceRef = NormalizeBranchRef(sourceBranch);
            url += $"&searchCriteria.sourceRefName={Uri.EscapeDataString(sourceRef)}";
        }
        url += "&api-version=7.1";

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoPullRequestListResponse, ct).ConfigureAwait(false);

        if (envelope?.Value is null)
        {
            return Array.Empty<AdoPullRequest>();
        }
        var mapped = new AdoPullRequest[envelope.Value.Length];
        for (int i = 0; i < envelope.Value.Length; i++)
        {
            mapped[i] = MapPullRequest(envelope.Value[i], organization, project, repository);
        }
        return mapped;
    }

    /// <inheritdoc />
    public async Task<AdoPullRequest?> GetPullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullrequests/{pullRequestId}" +
                  $"?api-version=7.1";

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoPullRequestRaw, ct).ConfigureAwait(false);
        return raw is null ? null : MapPullRequest(raw, organization, project, repository);
    }

    /// <inheritdoc />
    public async Task<AdoPullRequest?> CreatePullRequestAsync(
        string organization,
        string project,
        string repository,
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(sourceBranch);
        ArgumentException.ThrowIfNullOrEmpty(targetBranch);
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(description);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullrequests" +
                  $"?api-version=7.1";

        // Serialize once; the resulting string is reused across retries (HttpContent
        // is single-use, so a fresh StringContent is built per attempt below).
        var body = new AdoCreatePullRequestRequest
        {
            SourceRefName = NormalizeBranchRef(sourceBranch),
            TargetRefName = NormalizeBranchRef(targetBranch),
            Title = title,
            Description = description,
        };
        var bodyJson = JsonSerializer.Serialize(
            body, PolyphonyJsonContext.Default.AdoCreatePullRequestRequest);

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoPullRequestRaw, ct).ConfigureAwait(false);
        return raw is null ? null : MapPullRequest(raw, organization, project, repository);
    }

    /// <inheritdoc />
    public async Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
        string organization,
        string project,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repositoryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);

        // 1) Basic PR detail.
        var detailUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                        $"/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests/{pullRequestId}" +
                        $"?api-version=7.1";

        AdoPullRequestDetailRaw? detail;
        using (var detailResponse = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false))
        {
            if (detailResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // PR (or repo/project) does not exist — skip the reviewers call.
                return null;
            }
            await EnsureSuccessAsync(detailResponse, ct).ConfigureAwait(false);

            await using var detailStream = await detailResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            detail = await JsonSerializer.DeserializeAsync(
                detailStream, PolyphonyJsonContext.Default.AdoPullRequestDetailRaw, ct).ConfigureAwait(false);
        }

        if (detail is null)
        {
            // Unlikely — successful 200 with empty body. Treat as not found rather
            // than synthesise a half-populated record that would mislead callers.
            return null;
        }

        // 2) Reviewers list. A 404 here is unusual (the PR exists per step 1) but
        //    treat it the same as an empty reviewer set rather than failing the verb.
        var reviewersUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                           $"/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests/{pullRequestId}/reviewers" +
                           $"?api-version=7.1";

        AdoReviewerRaw[] reviewersRaw;
        using (var reviewersResponse = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, reviewersUrl);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false))
        {
            if (reviewersResponse.StatusCode == HttpStatusCode.NotFound)
            {
                reviewersRaw = Array.Empty<AdoReviewerRaw>();
            }
            else
            {
                await EnsureSuccessAsync(reviewersResponse, ct).ConfigureAwait(false);
                await using var reviewersStream =
                    await reviewersResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var envelope = await JsonSerializer.DeserializeAsync(
                    reviewersStream, PolyphonyJsonContext.Default.AdoReviewerListResponse, ct).ConfigureAwait(false);
                reviewersRaw = envelope?.Value ?? Array.Empty<AdoReviewerRaw>();
            }
        }

        return ComposePollData(detail, reviewersRaw);
    }

    /// <inheritdoc />
    public async Task<bool> SetPullRequestVoteAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string reviewerId,
        int vote,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);
        ArgumentException.ThrowIfNullOrEmpty(reviewerId);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                  $"/reviewers/{Uri.EscapeDataString(reviewerId)}?api-version=7.1";

        // Serialize the body once; HttpContent is single-use, so a fresh
        // StringContent is built per attempt by the request factory below.
        var body = new AdoSetReviewerVoteRequest { Vote = vote };
        var bodyJson = JsonSerializer.Serialize(
            body, PolyphonyJsonContext.Default.AdoSetReviewerVoteRequest);

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // PR or reviewer does not exist — treat as a structured "not found"
            // signal so the verb can emit pr_not_found rather than ado_failed.
            return false;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string lastMergeSourceCommitSha,
        AdoMergeStrategy mergeStrategy,
        bool deleteSourceBranch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);
        ArgumentException.ThrowIfNullOrEmpty(lastMergeSourceCommitSha);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                  $"?api-version=7.1";

        // Serialize the body once; HttpContent is single-use, so a fresh
        // StringContent is built per attempt by the request factory below.
        var body = new AdoCompletePullRequestRequest
        {
            Status = "completed",
            LastMergeSourceCommit = new AdoCommitRef { CommitId = lastMergeSourceCommitSha },
            CompletionOptions = new AdoCompletionOptions
            {
                MergeStrategy = MergeStrategyToWire(mergeStrategy),
                DeleteSourceBranch = deleteSourceBranch,
                BypassPolicy = false,
            },
        };
        var bodyJson = JsonSerializer.Serialize(
            body, PolyphonyJsonContext.Default.AdoCompletePullRequestRequest);

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        // Routable failure shapes are encoded into the result status —
        // throw only for unrecoverable wire-level errors so the verb can
        // route 404/409/400 distinctly without parsing exception messages.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new AdoCompletePullRequestResult(
                Status: "not_found",
                MergeCommitSha: null,
                HttpStatus: (int)response.StatusCode,
                ErrorBody: await ReadBodyTruncatedAsync(response, ct).ConfigureAwait(false));
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // ADO returns 409 for stale-head (lastMergeSourceCommit.commitId
            // doesn't match the current source tip) — the analogue of GitHub's
            // --match-head-commit refusal. Surface as a structured signal.
            return new AdoCompletePullRequestResult(
                Status: "stale_head",
                MergeCommitSha: null,
                HttpStatus: (int)response.StatusCode,
                ErrorBody: await ReadBodyTruncatedAsync(response, ct).ConfigureAwait(false));
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // 400 covers policy refusal, active conflicts, missing reviewers,
            // etc. — not retryable, but distinct from generic 5xx so the
            // verb can emit a more specific error_code.
            return new AdoCompletePullRequestResult(
                Status: "not_mergeable",
                MergeCommitSha: null,
                HttpStatus: (int)response.StatusCode,
                ErrorBody: await ReadBodyTruncatedAsync(response, ct).ConfigureAwait(false));
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var detail = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoPullRequestDetailRaw, ct).ConfigureAwait(false);
        var mergeCommit = detail?.LastMergeCommit?.CommitId;

        return new AdoCompletePullRequestResult(
            Status: "completed",
            MergeCommitSha: mergeCommit,
            HttpStatus: (int)response.StatusCode,
            ErrorBody: null);
    }

    /// <inheritdoc />
    public async Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string commentBody,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);
        ArgumentException.ThrowIfNullOrEmpty(commentBody);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                  $"/threads?api-version=7.1";

        // Serialize the body once; HttpContent is single-use, so a fresh
        // StringContent is built per attempt by the request factory below.
        var body = new AdoCreateThreadRequest
        {
            Comments = new List<AdoCreateThreadComment>
            {
                new()
                {
                    ParentCommentId = 0,
                    Content = commentBody,
                    CommentType = 1,
                },
            },
            Status = 4,
        };
        var bodyJson = JsonSerializer.Serialize(
            body, PolyphonyJsonContext.Default.AdoCreateThreadRequest);

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // PR or repo does not exist — return null so the verb emits
            // pr_not_found rather than ado_failed.
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoCreateThreadResponse, ct).ConfigureAwait(false);
        var firstComment = parsed?.Comments is { Count: > 0 } cs ? cs[0] : null;
        return new AdoCreateThreadResult(
            ThreadId: parsed?.Id ?? 0,
            CommentId: firstComment?.Id ?? 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                  $"/threads?api-version=7.1";

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // PR or repo does not exist — return null so the verb emits
            // pr_not_found rather than ado_failed.
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync(
            stream, PolyphonyJsonContext.Default.AdoThreadListResponse, ct).ConfigureAwait(false);

        if (envelope?.Value is null || envelope.Value.Length == 0)
        {
            return Array.Empty<AdoPullRequestThread>();
        }

        var mapped = new List<AdoPullRequestThread>(envelope.Value.Length);
        foreach (var threadRaw in envelope.Value)
        {
            // Filter tombstoned threads early — surviving consumers should
            // only see live discussion content.
            if (threadRaw.IsDeleted) continue;

            var projected = MapPullRequestThread(threadRaw);
            // After per-comment filtering a thread can end up with no
            // surviving comments (e.g. system-only thread). Drop it so the
            // verb's count reflects human content.
            if (projected.Comments.Count == 0) continue;

            mapped.Add(projected);
        }
        return mapped;
    }

    /// <inheritdoc />
    public async Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        // ── PR detail (description + existence probe) ────────────────────
        AdoPullRequestDetailRaw? detail;
        try
        {
            var detailUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                            $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                            $"?api-version=7.1";
            var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
            using var detailResponse = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                AddAuthHeaders(req, pat);
                return req;
            }, ct).ConfigureAwait(false);

            if (detailResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new AdoEvidenceFloorRead(
                    AdoEvidenceFloorOutcome.PrNotFound,
                    CommitCount: 0,
                    Body: string.Empty,
                    Detail: $"PR {pullRequestId} not found in {organization}/{project}/{repository}");
            }
            if (!detailResponse.IsSuccessStatusCode)
            {
                return new AdoEvidenceFloorRead(
                    AdoEvidenceFloorOutcome.AdoFailed,
                    CommitCount: 0,
                    Body: string.Empty,
                    Detail: await ComposeFailureDetailAsync(detailResponse, ct).ConfigureAwait(false));
            }
            await using var detailStream = await detailResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            detail = await JsonSerializer.DeserializeAsync(
                detailStream, PolyphonyJsonContext.Default.AdoPullRequestDetailRaw, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AdoEvidenceFloorRead(
                AdoEvidenceFloorOutcome.AdoFailed,
                CommitCount: 0,
                Body: string.Empty,
                Detail: $"PR detail call failed: {ex.Message}");
        }

        // ── Commit list (count beyond base) ───────────────────────────────
        int commitCount;
        try
        {
            var commitsUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                             $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                             $"/commits?api-version=7.1";
            var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
            using var commitsResponse = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, commitsUrl);
                AddAuthHeaders(req, pat);
                return req;
            }, ct).ConfigureAwait(false);

            if (commitsResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // PR existed at the detail call but the commits route 404'd
                // — treat as not-found for the floor read (a deleted PR
                // between the two calls would land here).
                return new AdoEvidenceFloorRead(
                    AdoEvidenceFloorOutcome.PrNotFound,
                    CommitCount: 0,
                    Body: string.Empty,
                    Detail: $"PR {pullRequestId} commits not found");
            }
            if (!commitsResponse.IsSuccessStatusCode)
            {
                return new AdoEvidenceFloorRead(
                    AdoEvidenceFloorOutcome.AdoFailed,
                    CommitCount: 0,
                    Body: string.Empty,
                    Detail: await ComposeFailureDetailAsync(commitsResponse, ct).ConfigureAwait(false));
            }
            await using var commitsStream = await commitsResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var commitsEnvelope = await JsonSerializer.DeserializeAsync(
                commitsStream, PolyphonyJsonContext.Default.AdoCommitListResponse, ct).ConfigureAwait(false);
            // Prefer the explicit count field; fall back to value.Count when
            // the server omits it (some api-versions do).
            commitCount = commitsEnvelope?.Count ?? commitsEnvelope?.Value?.Count ?? 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AdoEvidenceFloorRead(
                AdoEvidenceFloorOutcome.AdoFailed,
                CommitCount: 0,
                Body: string.Empty,
                Detail: $"PR commits call failed: {ex.Message}");
        }

        return new AdoEvidenceFloorRead(
            AdoEvidenceFloorOutcome.Found,
            CommitCount: commitCount,
            Body: detail?.Description ?? string.Empty,
            Detail: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        // ── 1. Find the latest iteration ─────────────────────────────────
        var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
        var iterationsUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                            $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                            $"/iterations?api-version=7.1";

        using var iterResp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, iterationsUrl);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (iterResp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(iterResp, ct).ConfigureAwait(false);

        await using var iterStream = await iterResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var iterEnvelope = await JsonSerializer.DeserializeAsync(
            iterStream, PolyphonyJsonContext.Default.AdoIterationListResponse, ct).ConfigureAwait(false);
        var latestIterationId = iterEnvelope?.Value?.Count > 0
            ? iterEnvelope.Value.Max(e => e.Id)
            : 0;
        if (latestIterationId == 0)
        {
            // PR exists but has no iterations — treat as no-changes (empty
            // list, not null). Differentiates from "PR not found".
            return Array.Empty<AdoPullRequestChangedFile>();
        }

        // ── 2. Read the per-file changes for that iteration ──────────────
        var changesUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                         $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                         $"/iterations/{latestIterationId}/changes?api-version=7.1";

        using var chgResp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, changesUrl);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (chgResp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(chgResp, ct).ConfigureAwait(false);

        await using var chgStream = await chgResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var chgEnvelope = await JsonSerializer.DeserializeAsync(
            chgStream, PolyphonyJsonContext.Default.AdoIterationChangesResponse, ct).ConfigureAwait(false);

        if (chgEnvelope?.ChangeEntries is null || chgEnvelope.ChangeEntries.Count == 0)
        {
            return Array.Empty<AdoPullRequestChangedFile>();
        }

        var mapped = new List<AdoPullRequestChangedFile>(chgEnvelope.ChangeEntries.Count);
        foreach (var entry in chgEnvelope.ChangeEntries)
        {
            var path = entry.Item?.Path;
            if (string.IsNullOrEmpty(path)) continue;
            // ADO paths are absolute (leading slash); normalise to repo-
            // relative for parity with the GitHub side.
            if (path.StartsWith('/')) path = path[1..];
            mapped.Add(new AdoPullRequestChangedFile(
                Path: path,
                Additions: -1, // ADO does not report; verb-side computes locally when needed.
                Deletions: -1,
                ChangeType: NormalizeChangeType(entry.ChangeType)));
        }
        return mapped;
    }

    /// <inheritdoc />
    public async Task<bool> EditPullRequestBodyAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);
        // Body may legitimately be empty (clearing the description) — no
        // ThrowIfNullOrEmpty here.
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                      $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                      $"?api-version=7.1";

            var bodyJson = JsonSerializer.Serialize(
                new AdoEditPullRequestRequest { Description = body },
                PolyphonyJsonContext.Default.AdoEditPullRequestRequest);

            using var response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                };
                AddAuthHeaders(req, pat);
                return req;
            }, ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Per the contract: false on any non-success outcome (timeout,
            // network, malformed JSON, missing PAT). Caller-driven
            // cancellation has already re-thrown above.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClosePullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string commentBeforeClose,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestId);

        // ── Optional comment first; best-effort (don't abort the close on
        //    comment failure — matches the gh CLI behaviour where
        //    `gh pr close --comment "x"` continues even if the comment leg
        //    has trouble). ───────────────────────────────────────────────
        if (!string.IsNullOrEmpty(commentBeforeClose))
        {
            try
            {
                _ = await CreatePullRequestCommentThreadAsync(
                    organization, project, repository, pullRequestId, commentBeforeClose, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort — swallow and proceed to the close PATCH.
            }
        }

        // ── Abandon PATCH ────────────────────────────────────────────────
        try
        {
            var pat = await ResolveAuthHeaderOrThrowAsync(ct).ConfigureAwait(false);
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                      $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}" +
                      $"?api-version=7.1";

            var bodyJson = JsonSerializer.Serialize(
                new AdoAbandonPullRequestRequest(),
                PolyphonyJsonContext.Default.AdoAbandonPullRequestRequest);

            using var response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                };
                AddAuthHeaders(req, pat);
                return req;
            }, ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Translate <see cref="AdoMergeStrategy"/> to the ADO REST wire-level
    /// <c>completionOptions.mergeStrategy</c> string.
    /// </summary>
    internal static string MergeStrategyToWire(AdoMergeStrategy strategy) => strategy switch
    {
        AdoMergeStrategy.NoFastForward => "noFastForward",
        AdoMergeStrategy.Squash => "squash",
        AdoMergeStrategy.Rebase => "rebase",
        AdoMergeStrategy.RebaseMerge => "rebaseMerge",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown ADO merge strategy."),
    };

    /// <summary>
    /// Normalise ADO's <c>VersionControlChangeType</c> string to a stable
    /// lower-case vocabulary. ADO can return comma-separated combinations
    /// (e.g. <c>"edit, rename"</c>); we keep the raw string trimmed and
    /// lower-cased so the verb side can grep substring (<c>contains
    /// "rename"</c>, etc.) without re-deriving casing.
    /// </summary>
    internal static string NormalizeChangeType(string? changeType)
    {
        if (string.IsNullOrWhiteSpace(changeType)) return "unknown";
        return changeType.Trim().ToLowerInvariant();
    }

    private static async Task<string> ComposeFailureDetailAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var snippet = await ReadBodyTruncatedAsync(response, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(snippet)
            ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}";
    }

    /// <summary>
    /// Map an <see cref="AdoThreadRaw"/> to its public <see cref="AdoPullRequestThread"/>
    /// projection. Comments are filtered (drop deleted + system) and the
    /// thread status is normalised to a stable lower-case vocabulary so the
    /// verb does not need to re-derive it.
    /// </summary>
    private static AdoPullRequestThread MapPullRequestThread(AdoThreadRaw raw)
    {
        var status = NormalizeThreadStatus(raw.Status);
        var comments = new List<AdoPullRequestComment>(raw.Comments?.Count ?? 0);
        if (raw.Comments is { } rawComments)
        {
            foreach (var c in rawComments)
            {
                if (c.IsDeleted) continue;
                var commentType = NormalizeCommentType(c.CommentType);
                // System comments are auto-generated by ADO ("branch updated",
                // "policy reset", reviewer added/removed, …) — never useful
                // for the human-review remediation path.
                if (string.Equals(commentType, "system", StringComparison.Ordinal)) continue;

                comments.Add(new AdoPullRequestComment
                {
                    Id = c.Id,
                    ParentCommentId = c.ParentCommentId,
                    Author = !string.IsNullOrEmpty(c.Author?.DisplayName)
                        ? c.Author!.DisplayName!
                        : (c.Author?.UniqueName ?? string.Empty),
                    Body = c.Content ?? string.Empty,
                    PublishedAt = c.PublishedDate,
                    LastUpdatedAt = c.LastUpdatedDate,
                    CommentType = commentType,
                });
            }
        }

        return new AdoPullRequestThread
        {
            Id = raw.Id,
            Status = status,
            IsResolved = IsResolvedThreadStatus(status),
            FilePath = raw.ThreadContext?.FilePath,
            Line = raw.ThreadContext?.RightFileStart?.Line is int line and > 0 ? line : null,
            Comments = comments,
        };
    }

    /// <summary>
    /// Normalise ADO's <c>CommentThreadStatus</c> to a stable lower-case
    /// string. Unknown / null / empty values fall through as <c>"unknown"</c>
    /// so consumers always have a non-empty status to match on.
    /// </summary>
    internal static string NormalizeThreadStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "unknown";
        // ADO returns the enum name in camelCase ("active", "wontFix"). The
        // first character is always lower; the camel-case humps are part of
        // the contract (we keep them so the field round-trips against ADO).
        return char.IsUpper(status[0])
            ? char.ToLowerInvariant(status[0]) + status.Substring(1)
            : status;
    }

    /// <summary>
    /// True when a (normalised) thread status indicates the conversation is
    /// resolved. Mirrors the GitHub <c>isResolved</c> bit on review threads
    /// so consumers can branch on a single boolean.
    /// </summary>
    internal static bool IsResolvedThreadStatus(string status) => status switch
    {
        "fixed" or "wontFix" or "closed" or "byDesign" => true,
        _ => false,
    };

    /// <summary>
    /// Normalise ADO's <c>CommentType</c> to a stable lower-case string.
    /// Unknown / null / empty values fall through as <c>"unknown"</c>.
    /// </summary>
    internal static string NormalizeCommentType(string? commentType)
    {
        if (string.IsNullOrWhiteSpace(commentType)) return "unknown";
        return char.IsUpper(commentType[0])
            ? char.ToLowerInvariant(commentType[0]) + commentType.Substring(1)
            : commentType;
    }

    private static async Task<string?> ReadBodyTruncatedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrEmpty(body) ? null : Truncate(body, 200);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Map the wire-level PR detail + reviewer envelope into the
    /// platform-neutral <see cref="AdoPullRequestPollData"/> projection.
    /// State, mergeability, and the aggregated review decision are all
    /// normalised here so the verb has nothing left to interpret.
    /// </summary>
    private static AdoPullRequestPollData ComposePollData(
        AdoPullRequestDetailRaw detail,
        IReadOnlyList<AdoReviewerRaw> reviewersRaw)
    {
        var reviews = new AdoPullRequestReview[reviewersRaw.Count];
        for (int i = 0; i < reviewersRaw.Count; i++)
        {
            var r = reviewersRaw[i];
            reviews[i] = new AdoPullRequestReview
            {
                Identity = !string.IsNullOrEmpty(r.DisplayName)
                    ? r.DisplayName
                    : (r.UniqueName ?? string.Empty),
                Vote = MapVote(r.Vote),
                SubmittedAt = null, // ADO does not surface a per-reviewer timestamp.
            };
        }

        var state = MapState(detail.Status);
        var isMerged = string.Equals(state, "MERGED", StringComparison.Ordinal);

        return new AdoPullRequestPollData
        {
            Number = detail.PullRequestId,
            State = state,
            ReviewDecision = AggregateReviewDecision(reviewersRaw),
            Mergeable = AdoMergeStatus.Map(detail.MergeStatus),
            HeadRefName = StripRefsHeads(detail.SourceRefName) ?? string.Empty,
            HeadRefOid = detail.LastMergeSourceCommit?.CommitId ?? string.Empty,
            BaseRefName = StripRefsHeads(detail.TargetRefName) ?? string.Empty,
            MergedAt = isMerged ? detail.ClosedDate : null,
            MergeCommit = isMerged ? detail.LastMergeCommit?.CommitId : null,
            Body = detail.Description ?? string.Empty,
            AuthorIdentity = !string.IsNullOrEmpty(detail.CreatedBy?.DisplayName)
                ? detail.CreatedBy!.DisplayName!
                : (detail.CreatedBy?.UniqueName ?? string.Empty),
            Reviews = reviews,
        };
    }

    /// <summary>
    /// Map ADO's vote enum to the platform-neutral string vocabulary.
    /// Unknown ints fall through as <c>"no_vote"</c> rather than throwing —
    /// the verb's job is to report status, not validate the ADO API.
    /// </summary>
    internal static string MapVote(int vote) => vote switch
    {
        10 => "approved",
        5 => "approved_with_suggestions",
        -5 => "waiting_for_author",
        -10 => "rejected",
        _ => "no_vote",
    };

    /// <summary>
    /// Map ADO PR <c>status</c> to the platform-neutral state vocabulary
    /// (<c>OPEN | MERGED | CLOSED</c>). Unknown values fall through as
    /// <c>OPEN</c> so the verb still has a usable signal.
    /// </summary>
    internal static string MapState(string? status) => status switch
    {
        "active" => "OPEN",
        "completed" => "MERGED",
        "abandoned" => "CLOSED",
        _ => "OPEN",
    };

    /// <summary>
    /// Aggregate individual reviewer votes into a single decision string.
    /// Any rejection (-10) shortcircuits to <c>REJECTED</c>; otherwise the
    /// decision is <c>APPROVED</c> when there is at least one required
    /// reviewer and every required reviewer voted approved (10) or approved
    /// with suggestions (5). All other shapes (no required reviewers, mixed
    /// votes, waiting-for-author) are <c>REVIEW_REQUIRED</c>.
    /// </summary>
    internal static string AggregateReviewDecision(IReadOnlyList<AdoReviewerRaw> reviewers)
    {
        var anyRejection = false;
        var requiredCount = 0;
        var requiredApproved = 0;
        for (int i = 0; i < reviewers.Count; i++)
        {
            var r = reviewers[i];
            if (r.Vote == -10) anyRejection = true;
            if (r.IsRequired)
            {
                requiredCount++;
                if (r.Vote >= 5) requiredApproved++;
            }
        }
        if (anyRejection) return "REJECTED";
        if (requiredCount > 0 && requiredApproved == requiredCount) return "APPROVED";
        return "REVIEW_REQUIRED";
    }

    /// <summary>
    /// Strip the <c>refs/heads/</c> prefix when present so the JSON envelope
    /// matches the GitHub side (gh returns short branch names from
    /// <c>--json headRefName</c>).
    /// </summary>
    private static string? StripRefsHeads(string? refName)
    {
        if (string.IsNullOrEmpty(refName)) return refName;
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.Ordinal)
            ? refName.Substring(prefix.Length)
            : refName;
    }

    private static string StatusToQueryValue(AdoPullRequestStatus status) => status switch
    {
        AdoPullRequestStatus.Active => "active",
        AdoPullRequestStatus.Completed => "completed",
        AdoPullRequestStatus.Abandoned => "abandoned",
        AdoPullRequestStatus.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown PR status filter."),
    };

    /// <summary>
    /// Throw <see cref="HttpRequestException"/> for any non-success status (the
    /// caller has already handled 404). Status code is propagated on the
    /// exception so callers can branch on it (.NET 8+ behaviour).
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort body capture for the error message; ignore failures.
        }
        var snippet = string.IsNullOrEmpty(body)
            ? string.Empty
            : $" — {Truncate(body, 200)}";
        throw new HttpRequestException(
            $"ADO request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{snippet}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// Send an HTTP request with the configured retry-on-timeout policy.
    /// Throws <see cref="TimeoutException"/> when every attempt timed out.
    /// HTTP failure status codes are returned to the caller (no retry).
    /// </summary>
    /// <remarks>
    /// The factory delegate is required because <see cref="HttpRequestMessage"/>
    /// instances are single-use; a retry needs a fresh request.
    /// </remarks>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_policy.PerAttemptTimeout);

            HttpRequestMessage? request = null;
            try
            {
                request = requestFactory();
                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                request?.Dispose();
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout fired.
                request?.Dispose();
                if (attempt >= _policy.MaxAttempts)
                {
                    throw new TimeoutException(
                        $"ADO request timed out after {_policy.MaxAttempts} attempt(s) " +
                        $"of {_policy.PerAttemptTimeout.TotalSeconds:F0}s each.");
                }
                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
            catch
            {
                request?.Dispose();
                throw;
            }
        }

        // Unreachable: the loop always either returns or throws above.
        throw new InvalidOperationException("SendWithRetryAsync exited the retry loop without returning.");
    }

    private async Task DelayBackoffAsync(int attemptJustFailed, CancellationToken ct)
    {
        if (_policy.InitialBackoff <= TimeSpan.Zero) return;
        // 1s, 2s, 4s, ... — no jitter (single-process CLI).
        var multiplier = 1L << (attemptJustFailed - 1);
        var delay = TimeSpan.FromTicks(_policy.InitialBackoff.Ticks * multiplier);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
