using System.Net;
using System.Text;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// Coverage for the PR list/get/create verbs added to <see cref="IAdoClient"/>.
///
/// <para>
/// Each verb exercises the same six axes:
/// success, 401, 404, 5xx, timeout exhaustion, and caller cancellation.
/// The handler stub mirrors the one in <see cref="AdoClientTests"/> rather than
/// reusing it, to keep each test file self-contained.
/// </para>
/// </summary>
public sealed class AdoClientPullRequestsTests
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

    private static IPolyphonyAuthProvider TokenResolver(string? token) =>
        new PatAuthProvider(new AdoTokenResolver(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]));

    private static AdoClient NewClient(StubHandler handler, string? pat = "real-pat",
        AdoClientPolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new AdoClient(http, TokenResolver(pat), policy ?? AdoClientPolicy.NoRetry);
    }

    private const string SinglePrBody = """
        {
          "pullRequestId": 42,
          "title": "Fix login bug",
          "description": "Body text",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "status": "active",
          "mergeStatus": "succeeded",
          "createdBy": { "displayName": "Ada Lovelace" },
          "creationDate": "2024-01-15T08:00:00Z",
          "url": "https://dev.azure.com/myorg/_apis/git/repositories/myrepo/pullRequests/42",
          "_links": {
            "web": { "href": "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42" }
          }
        }
        """;

    private const string ListBody = """
        {
          "count": 2,
          "value": [
            {
              "pullRequestId": 1,
              "title": "First",
              "description": "first body",
              "sourceRefName": "refs/heads/feature/a",
              "targetRefName": "refs/heads/main",
              "status": "active",
              "mergeStatus": "succeeded",
              "createdBy": { "displayName": "Alice" },
              "creationDate": "2024-01-01T00:00:00Z",
              "url": "https://dev.azure.com/myorg/_apis/git/repositories/myrepo/pullRequests/1",
              "_links": { "web": { "href": "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/1" } }
            },
            {
              "pullRequestId": 2,
              "title": "Second",
              "description": "",
              "sourceRefName": "refs/heads/feature/b",
              "targetRefName": "refs/heads/main",
              "status": "active",
              "mergeStatus": null,
              "createdBy": { "displayName": "Bob" },
              "creationDate": "2024-01-02T00:00:00Z",
              "url": "https://dev.azure.com/myorg/_apis/git/repositories/myrepo/pullRequests/2",
              "_links": { "web": { "href": "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/2" } }
            }
          ]
        }
        """;

    // ─── Branch normalization helper ─────────────────────────────────────

    [Theory]
    [InlineData("feature/x", "refs/heads/feature/x")]
    [InlineData("main", "refs/heads/main")]
    [InlineData("refs/heads/feature/x", "refs/heads/feature/x")]
    [InlineData("refs/pull/1/merge", "refs/pull/1/merge")]
    public void NormalizeBranchRef_RewritesShortNames_PassesThroughFullRefs(string input, string expected)
    {
        AdoClient.NormalizeBranchRef(input).ShouldBe(expected);
    }

    // ─── ListPullRequestsAsync ───────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestsAsync_HappyPath_ParsesMultiplePrs()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, ListBody);
        var client = NewClient(handler);

        var prs = await client.ListPullRequestsAsync(Org, Project, Repo);

        prs.ShouldNotBeNull();
        prs!.Count.ShouldBe(2);
        prs[0].PullRequestId.ShouldBe(1);
        prs[0].Title.ShouldBe("First");
        prs[0].SourceRefName.ShouldBe("refs/heads/feature/a");
        prs[0].CreatedBy.ShouldBe("Alice");
        prs[0].MergeStatus.ShouldBe("succeeded");
        prs[0].Url.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/1");
        prs[1].PullRequestId.ShouldBe(2);
        prs[1].MergeStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestsAsync_HitsExpectedUrl()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, ListBody);
        var client = NewClient(handler);

        await client.ListPullRequestsAsync(Org, Project, Repo, AdoPullRequestStatus.Completed);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        var uri = handler.LastRequest.RequestUri!.AbsoluteUri;
        uri.ShouldStartWith("https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullrequests");
        uri.ShouldContain("searchCriteria.status=completed");
        uri.ShouldContain("api-version=7.1");
    }

    [Fact]
    public async Task ListPullRequestsAsync_SendsBasicAuthWithPat()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, ListBody);
        var client = NewClient(handler, pat: "my-pat");

        await client.ListPullRequestsAsync(Org, Project, Repo);

        var auth = handler.LastRequest!.Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!))
            .ShouldBe(":my-pat");
    }

    [Fact]
    public async Task ListPullRequestsAsync_EmptyValue_ReturnsEmptyList()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{"count":0,"value":[]}""");
        var client = NewClient(handler);

        var prs = await client.ListPullRequestsAsync(Org, Project, Repo);

        prs.ShouldNotBeNull();
        prs!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPullRequestsAsync_404_ReturnsNull()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var prs = await client.ListPullRequestsAsync(Org, Project, Repo);

        prs.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestsAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.ListPullRequestsAsync(Org, Project, Repo));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListPullRequestsAsync_5xx_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.ListPullRequestsAsync(Org, Project, Repo));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        // 5xx is treated as a real signal — single attempt, no retry.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task ListPullRequestsAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.ListPullRequestsAsync(Org, Project, Repo));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task ListPullRequestsAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.ListPullRequestsAsync(Org, Project, Repo));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    // ─── GetPullRequestAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestAsync_HappyPath_ReturnsMappedPr()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SinglePrBody);
        var client = NewClient(handler);

        var pr = await client.GetPullRequestAsync(Org, Project, Repo, 42);

        pr.ShouldNotBeNull();
        pr!.PullRequestId.ShouldBe(42);
        pr.Title.ShouldBe("Fix login bug");
        pr.Description.ShouldBe("Body text");
        pr.SourceRefName.ShouldBe("refs/heads/feature/x");
        pr.TargetRefName.ShouldBe("refs/heads/main");
        pr.Status.ShouldBe("active");
        pr.MergeStatus.ShouldBe("succeeded");
        pr.CreatedBy.ShouldBe("Ada Lovelace");
        pr.CreationDate.ShouldBe(new DateTime(2024, 1, 15, 8, 0, 0, DateTimeKind.Utc));
        pr.Url.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
    }

    [Fact]
    public async Task GetPullRequestAsync_HitsExpectedUrl()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SinglePrBody);
        var client = NewClient(handler);

        await client.GetPullRequestAsync(Org, Project, Repo, 42);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullrequests/42?api-version=7.1");
    }

    [Fact]
    public async Task GetPullRequestAsync_404_ReturnsNull()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var pr = await client.GetPullRequestAsync(Org, Project, Repo, 999);

        pr.ShouldBeNull();
    }

    [Fact]
    public async Task GetPullRequestAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetPullRequestAsync(Org, Project, Repo, 42));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPullRequestAsync_5xx_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.InternalServerError, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetPullRequestAsync(Org, Project, Repo, 42));
        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPullRequestAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 2,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.GetPullRequestAsync(Org, Project, Repo, 42));
        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetPullRequestAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.GetPullRequestAsync(Org, Project, Repo, 42));
    }

    // ─── CreatePullRequestAsync ──────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequestAsync_HappyPath_PostsAndReturnsCreatedPr()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Created, SinglePrBody);
        var client = NewClient(handler);

        var pr = await client.CreatePullRequestAsync(
            Org, Project, Repo,
            sourceBranch: "refs/heads/feature/x",
            targetBranch: "refs/heads/main",
            title: "Fix login bug",
            description: "Body text");

        pr.ShouldNotBeNull();
        pr!.PullRequestId.ShouldBe(42);
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullrequests?api-version=7.1");
    }

    [Fact]
    public async Task CreatePullRequestAsync_NormalizesShortBranchNames()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Created, SinglePrBody);
        var client = NewClient(handler);

        await client.CreatePullRequestAsync(
            Org, Project, Repo,
            sourceBranch: "feature/x",       // short — should be normalized
            targetBranch: "main",            // short — should be normalized
            title: "T",
            description: "D");

        var body = handler.LastRequestBody.ShouldNotBeNull();
        body.ShouldContain("\"sourceRefName\":\"refs/heads/feature/x\"");
        body.ShouldContain("\"targetRefName\":\"refs/heads/main\"");
    }

    [Fact]
    public async Task CreatePullRequestAsync_PassesThroughFullRefs()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Created, SinglePrBody);
        var client = NewClient(handler);

        await client.CreatePullRequestAsync(
            Org, Project, Repo,
            sourceBranch: "refs/heads/feature/x",
            targetBranch: "refs/heads/main",
            title: "T",
            description: "D");

        var body = handler.LastRequestBody.ShouldNotBeNull();
        body.ShouldContain("\"sourceRefName\":\"refs/heads/feature/x\"");
        body.ShouldContain("\"targetRefName\":\"refs/heads/main\"");
    }

    [Fact]
    public async Task CreatePullRequestAsync_SerializesTitleAndDescription()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Created, SinglePrBody);
        var client = NewClient(handler);

        await client.CreatePullRequestAsync(
            Org, Project, Repo,
            sourceBranch: "feature/x",
            targetBranch: "main",
            title: "My title",
            description: "My description");

        var body = handler.LastRequestBody.ShouldNotBeNull();
        body.ShouldContain("\"title\":\"My title\"");
        body.ShouldContain("\"description\":\"My description\"");
        handler.LastRequestContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task CreatePullRequestAsync_404_ReturnsNull()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var pr = await client.CreatePullRequestAsync(
            Org, Project, Repo, "feature/x", "main", "T", "D");

        pr.ShouldBeNull();
    }

    [Fact]
    public async Task CreatePullRequestAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CreatePullRequestAsync(
                Org, Project, Repo, "feature/x", "main", "T", "D"));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePullRequestAsync_5xx_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.ServiceUnavailable, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CreatePullRequestAsync(
                Org, Project, Repo, "feature/x", "main", "T", "D"));
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.CreatePullRequestAsync(
                Org, Project, Repo, "feature/x", "main", "T", "D"));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task CreatePullRequestAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.CreatePullRequestAsync(
                Org, Project, Repo, "feature/x", "main", "T", "D"));
    }

    [Fact]
    public async Task CreatePullRequestAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.CreatePullRequestAsync(
                Org, Project, Repo, "feature/x", "main", "T", "D", cts.Token));
    }

    // ─── Test fake ──────────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastRequestContentType { get; private set; }
        public int RequestCount { get; private set; }

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

        public static StubHandler AlwaysFail() => new((_, _) =>
            throw new InvalidOperationException("handler should not be invoked"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            // Materialise the request body before responding so tests can inspect it
            // even after the response (and its associated request) have been disposed.
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastRequestContentType = request.Content.Headers.ContentType?.MediaType;
            }
            return await _respond(request, cancellationToken);
        }
    }
}
