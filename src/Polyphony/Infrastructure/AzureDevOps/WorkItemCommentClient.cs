using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Default <see cref="IWorkItemCommentClient"/> backed by <see cref="HttpClient"/>.
/// Mirrors the retry + auth infrastructure of <see cref="AdoClient"/> — same
/// policy shape, same PAT resolution, same error semantics.
/// </summary>
public sealed class WorkItemCommentClient : IWorkItemCommentClient
{
    private readonly HttpClient _http;
    private readonly AdoTokenResolver _tokenResolver;
    private readonly AdoClientPolicy _policy;

    public WorkItemCommentClient(HttpClient http, AdoTokenResolver tokenResolver)
        : this(http, tokenResolver, AdoClientPolicy.Default)
    {
    }

    /// <summary>Test constructor accepting an explicit policy.</summary>
    public WorkItemCommentClient(HttpClient http, AdoTokenResolver tokenResolver, AdoClientPolicy policy)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdoWorkItemComment>> ListCommentsAsync(
        string organization,
        string project,
        int workItemId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemId);

        var pat = ResolvePatOrThrow();
        var all = new List<AdoWorkItemComment>();
        string? continuationToken = null;

        do
        {
            var url = BuildListUrl(organization, project, workItemId, continuationToken);

            using var response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeaders(req, pat);
                return req;
            }, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return all;

            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync(
                stream, PolyphonyJsonContext.Default.AdoWorkItemCommentListResponse, ct).ConfigureAwait(false);

            if (envelope?.Comments is { } comments)
            {
                foreach (var raw in comments)
                {
                    all.Add(new AdoWorkItemComment
                    {
                        WorkItemId = workItemId,
                        CommentId = raw.Id,
                        Text = raw.Text ?? string.Empty,
                        CreatedBy = raw.CreatedBy?.DisplayName
                            ?? raw.CreatedBy?.UniqueName
                            ?? string.Empty,
                        CreatedDate = raw.CreatedDate,
                    });
                }
            }

            continuationToken = envelope?.ContinuationToken;
        } while (!string.IsNullOrEmpty(continuationToken));

        return all;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCommentAsync(
        string organization,
        string project,
        int workItemId,
        long commentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemId);

        var pat = ResolvePatOrThrow();
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/wit/workItems/{workItemId}/comments/{commentId}?api-version=7.1-preview.4";

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            AddAuthHeaders(req, pat);
            return req;
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    private static string BuildListUrl(string organization, string project, int workItemId, string? continuationToken)
    {
        var sb = new StringBuilder();
        sb.Append("https://dev.azure.com/")
          .Append(Uri.EscapeDataString(organization))
          .Append('/')
          .Append(Uri.EscapeDataString(project))
          .Append("/_apis/wit/workItems/")
          .Append(workItemId)
          .Append("/comments?api-version=7.1-preview.4&$top=200");

        if (!string.IsNullOrEmpty(continuationToken))
            sb.Append("&continuationToken=").Append(Uri.EscapeDataString(continuationToken));

        return sb.ToString();
    }

    private static void AddAuthHeaders(HttpRequestMessage request, string pat)
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

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

        throw new InvalidOperationException("SendWithRetryAsync exited the retry loop without returning.");
    }

    private async Task DelayBackoffAsync(int attemptJustFailed, CancellationToken ct)
    {
        if (_policy.InitialBackoff <= TimeSpan.Zero) return;
        var multiplier = 1L << (attemptJustFailed - 1);
        var delay = TimeSpan.FromTicks(_policy.InitialBackoff.Ticks * multiplier);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

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
            // Best-effort body capture for the error message.
        }
        var snippet = string.IsNullOrEmpty(body)
            ? string.Empty
            : $" — {(body.Length <= 200 ? body : body.Substring(0, 200) + "…")}";
        throw new HttpRequestException(
            $"ADO request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{snippet}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
