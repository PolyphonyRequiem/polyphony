using System.Net;
using System.Text;
using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// Coverage for <see cref="IAdoClient.CreatePullRequestCommentThreadAsync"/> —
/// the POST that creates a single advisory comment thread on an ADO pull
/// request. Failure shape mirrors the other PR verbs: 404 → <c>null</c>
/// (structured "not found"); 401/403/5xx → <see cref="HttpRequestException"/>;
/// timeout → <see cref="TimeoutException"/>; missing PAT →
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class AdoClientCreateThreadTests
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;
    private const string CommentBody = "Looks good — shipping.";

    private static IPolyphonyAuthProvider TokenResolver(string? token) =>
        new PatAuthProvider(new AdoTokenResolver(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]));

    private static AdoClient NewClient(StubHandler handler, string? pat = "real-pat",
        AdoClientPolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new AdoClient(http, TokenResolver(pat), policy ?? AdoClientPolicy.NoRetry);
    }

    private const string OkBody = """
        {
          "id": 1234,
          "status": "closed",
          "comments": [
            { "id": 5678, "parentCommentId": 0, "content": "...", "commentType": "text" }
          ]
        }
        """;

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_HappyPath_ReturnsThreadAndCommentIds()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        var result = await client.CreatePullRequestCommentThreadAsync(
            Org, Project, Repo, PrId, CommentBody);

        result.ShouldNotBeNull();
        result!.ThreadId.ShouldBe(1234);
        result.CommentId.ShouldBe(5678);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_HitsExpectedUrlAndMethod()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        await client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody);

        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Post);
        req.RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullRequests/42/threads?api-version=7.1");
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_SendsBasicAuthAndCorrectBody()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler, pat: "my-pat");

        await client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody);

        var req = handler.Requests[0];
        var auth = req.Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!))
            .ShouldBe(":my-pat");

        // Body must match { comments: [ { parentCommentId: 0, content, commentType: 1 } ], status: 4 }.
        var body = handler.RequestBodies[0];
        body.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(4);
        var comments = doc.RootElement.GetProperty("comments");
        comments.GetArrayLength().ShouldBe(1);
        var first = comments[0];
        first.GetProperty("parentCommentId").GetInt32().ShouldBe(0);
        first.GetProperty("content").GetString().ShouldBe(CommentBody);
        first.GetProperty("commentType").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_EscapesRepositoryInPath()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        await client.CreatePullRequestCommentThreadAsync(
            Org, Project, "weird repo/with slash", PrId, CommentBody);

        handler.Requests[0].RequestUri!.AbsoluteUri
            .ShouldContain("weird%20repo%2Fwith%20slash");
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_PreservesMultilineCommentBody()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);
        var multiLine = "line 1\nline 2\n\n```code```";

        await client.CreatePullRequestCommentThreadAsync(
            Org, Project, Repo, PrId, multiLine);

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]!);
        doc.RootElement.GetProperty("comments")[0]
            .GetProperty("content").GetString().ShouldBe(multiLine);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_ResponseMissingComments_ReturnsZeroIds()
    {
        const string responseWithoutComments = """{ "id": 99 }""";
        var handler = StubHandler.Returns(HttpStatusCode.OK, responseWithoutComments);
        var client = NewClient(handler);

        var result = await client.CreatePullRequestCommentThreadAsync(
            Org, Project, Repo, PrId, CommentBody);

        result.ShouldNotBeNull();
        result!.ThreadId.ShouldBe(99);
        result.CommentId.ShouldBe(0);
    }

    // ─── Failure axes ────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_404_ReturnsNull()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var result = await client.CreatePullRequestCommentThreadAsync(
            Org, Project, Repo, PrId, CommentBody);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_403_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_5xx_ThrowsHttpRequestExceptionWithoutRetry()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        // 5xx is treated as a real signal — single attempt, no retry.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, CommentBody));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_EmptyOrganization_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CreatePullRequestCommentThreadAsync("", Project, Repo, PrId, CommentBody));
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_EmptyProject_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, "", Repo, PrId, CommentBody));
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_EmptyRepository_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, "", PrId, CommentBody));
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_EmptyBody_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, PrId, ""));
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_NonPositivePrId_ThrowsArgumentOutOfRange()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.CreatePullRequestCommentThreadAsync(Org, Project, Repo, 0, CommentBody));
    }

    [Fact]
    public async Task CreatePullRequestCommentThreadAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.CreatePullRequestCommentThreadAsync(
                Org, Project, Repo, PrId, CommentBody, cts.Token));
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
