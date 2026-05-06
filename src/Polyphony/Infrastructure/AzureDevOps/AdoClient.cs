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
        // ":pat" — PAT is the password, username is empty.
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

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
