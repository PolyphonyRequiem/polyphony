using System.Net;
using System.Text;
using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// Coverage for <see cref="IAdoClient.SetPullRequestVoteAsync"/> — the
/// PATCH that submits a reviewer's vote on an ADO pull request. Failure
/// shape mirrors the other PR verbs: 404 → <c>false</c> (structured "not
/// found"); 401/403/5xx → <see cref="HttpRequestException"/>; timeout →
/// <see cref="TimeoutException"/>; missing PAT → <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class AdoClientVoteTests
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;
    private const string ReviewerId = "11111111-2222-3333-4444-555555555555";

    private static IPolyphonyAuthProvider TokenResolver(string? token) =>
        new PatAuthProvider(new AdoTokenResolver(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]));

    private static AdoClient NewClient(StubHandler handler, string? pat = "real-pat",
        AdoClientPolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new AdoClient(http, TokenResolver(pat), policy ?? AdoClientPolicy.NoRetry);
    }

    private const string OkBody = """
        { "reviewerUrl": "...", "vote": 10, "displayName": "Alice" }
        """;

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetPullRequestVoteAsync_HappyPath_ReturnsTrue()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        var ok = await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10);

        ok.ShouldBeTrue();
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_HitsExpectedUrlAndMethod()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10);

        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Patch);
        req.RequestUri!.AbsoluteUri.ShouldBe(
            $"https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullRequests/42/reviewers/{ReviewerId}?api-version=7.1");
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_SendsBasicAuthAndJsonBody()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler, pat: "my-pat");

        await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, -10);

        var req = handler.Requests[0];
        var auth = req.Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!))
            .ShouldBe(":my-pat");

        // Body must be { "vote": -10 } — only the vote field is sent.
        var body = handler.RequestBodies[0];
        body.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("vote").GetInt32().ShouldBe(-10);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-10)]
    public async Task SetPullRequestVoteAsync_AcceptsAllAdoVoteValues(int vote)
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        var ok = await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, vote);

        ok.ShouldBeTrue();
        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("vote").GetInt32().ShouldBe(vote);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_EscapesReviewerIdInPath()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        // Reviewer ids in ADO are GUIDs, but the client must still URI-escape
        // to be defensive against future non-GUID identifiers.
        await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, "weird id/with slash", 10);

        handler.Requests[0].RequestUri!.AbsoluteUri.ShouldContain("weird%20id%2Fwith%20slash");
    }

    // ─── Failure axes ────────────────────────────────────────────────────

    [Fact]
    public async Task SetPullRequestVoteAsync_404_ReturnsFalse()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var ok = await client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10);

        ok.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_403_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_5xx_ThrowsHttpRequestExceptionWithoutRetry()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        // 5xx is treated as a real signal — single attempt, no retry.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task SetPullRequestVoteAsync_EmptyOrganization_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.SetPullRequestVoteAsync("", Project, Repo, PrId, ReviewerId, 10));
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_EmptyReviewerId_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, "", 10));
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_NonPositivePrId_ThrowsArgumentOutOfRange()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, 0, ReviewerId, 10));
    }

    [Fact]
    public async Task SetPullRequestVoteAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.SetPullRequestVoteAsync(Org, Project, Repo, PrId, ReviewerId, 10, cts.Token));
    }

    // ─── Test fake (records request bodies, single response) ─────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> RequestBodies { get; } = new();
        public int RequestCount => Requests.Count;

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public static StubHandler Returns(HttpStatusCode status, string body) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        public static StubHandler Hangs() =>
            new(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        public static StubHandler AlwaysFail() =>
            new((_, _) => throw new InvalidOperationException("handler should not be invoked"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // Capture the body before the handler swap consumes it (StringContent
            // is replayable, but reading after the response is sent is awkward).
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            RequestBodies.Add(body);
            return await _respond(request, cancellationToken);
        }
    }
}
