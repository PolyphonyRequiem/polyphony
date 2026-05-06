using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
/// Authentication uses Basic auth with the PAT as the password and an empty
/// username (<c>Authorization: Basic base64(":pat")</c>) — the format the
/// connection-data endpoint accepts. The PAT is resolved per-request from
/// <see cref="AdoTokenResolver"/> so a token rotated mid-process is picked
/// up on the next call.
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
    private readonly AdoTokenResolver _tokenResolver;
    private readonly AdoClientPolicy _policy;

    /// <summary>Production constructor; uses <see cref="AdoClientPolicy.Default"/>.</summary>
    public AdoClient(HttpClient http, AdoTokenResolver tokenResolver)
        : this(http, tokenResolver, AdoClientPolicy.Default)
    {
    }

    /// <summary>Test constructor accepting an explicit policy.</summary>
    public AdoClient(HttpClient http, AdoTokenResolver tokenResolver, AdoClientPolicy policy)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public async Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
    {
        var pat = _tokenResolver.Resolve();
        if (string.IsNullOrEmpty(pat))
        {
            return new AdoAuthStatus(
                IsAuthenticated: false,
                Detail: "No PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login')",
                OrganizationName: null);
        }

        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildProbeRequest(pat), ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new AdoAuthStatus(
                    IsAuthenticated: false,
                    Detail: "PAT rejected",
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

            // 200 with body: PAT is valid. Surface the display name when present so
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

    private static HttpRequestMessage BuildProbeRequest(string pat)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ConnectionDataUrl);
        AddAuthHeaders(request, pat);
        return request;
    }

    /// <summary>
    /// Apply Basic auth + JSON Accept headers to a request. Centralised so
    /// every verb sends the exact same wire-level shape (the connection-data
    /// probe established the format; PR verbs reuse it verbatim).
    /// </summary>
    private static void AddAuthHeaders(HttpRequestMessage request, string pat)
    {
        // ":pat" — PAT is the password, username is empty.
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Resolve the PAT or throw. Used by operational verbs (PR list/get/create)
    /// that have no graceful "unauthenticated" projection — unlike the auth
    /// probe, which encodes "no PAT" as a status outcome.
    /// </summary>
    private string ResolvePatOrThrow()
    {
        var pat = _tokenResolver.Resolve();
        if (string.IsNullOrEmpty(pat))
        {
            throw new InvalidOperationException(
                "No ADO PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login').");
        }
        return pat;
    }

    /// <summary>
    /// Map the wire-level <see cref="AdoPullRequestRaw"/> shape to the public
    /// <see cref="AdoPullRequest"/> projection. Prefers <c>_links.web.href</c>
    /// for the URL (canonical user-facing page); falls back to the raw API
    /// <c>url</c> when the links envelope is absent.
    /// </summary>
    private static AdoPullRequest MapPullRequest(AdoPullRequestRaw raw)
    {
        var url = raw.Links?.Web?.Href ?? raw.Url ?? "";
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
            Url: url);
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
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(repository);

        var pat = ResolvePatOrThrow();
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullrequests" +
                  $"?searchCriteria.status={StatusToQueryValue(status)}&api-version=7.1";

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
            mapped[i] = MapPullRequest(envelope.Value[i]);
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

        var pat = ResolvePatOrThrow();
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
        return raw is null ? null : MapPullRequest(raw);
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

        var pat = ResolvePatOrThrow();
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
        return raw is null ? null : MapPullRequest(raw);
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
