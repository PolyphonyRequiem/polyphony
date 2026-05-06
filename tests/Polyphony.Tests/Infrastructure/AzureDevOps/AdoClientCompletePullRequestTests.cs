using System.Net;
using System.Text;
using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// Coverage for <see cref="IAdoClient.CompletePullRequestAsync"/> — the
/// PATCH that merges an ADO pull request. Per ADR Rev 4 the strategy is
/// pinned to <c>noFastForward</c> and the head SHA is supplied as
/// <c>lastMergeSourceCommit.commitId</c> (the ADO analogue of GitHub's
/// <c>--match-head-commit</c> stale-head guard). Failure shape:
/// 200 → <c>completed</c> with merge SHA; 404 → <c>not_found</c>; 409 →
/// <c>stale_head</c>; 400 → <c>not_mergeable</c>; 401/403/5xx →
/// <see cref="HttpRequestException"/>; timeout →
/// <see cref="TimeoutException"/>; missing PAT →
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class AdoClientCompletePullRequestTests
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;
    private const string HeadSha = "deadbeefcafe1234567890abcdef1234567890ab";
    private const string MergeSha = "abc1234567890def1234567890abcdef12345678";

    private static AdoTokenResolver TokenResolver(string? token) =>
        new(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]);

    private static AdoClient NewClient(StubHandler handler, string? pat = "real-pat",
        AdoClientPolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new AdoClient(http, TokenResolver(pat), policy ?? AdoClientPolicy.NoRetry);
    }

    private static string SuccessBody(string mergeSha = MergeSha) => $$"""
        {
          "pullRequestId": {{PrId}},
          "status": "completed",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "{{HeadSha}}" },
          "lastMergeCommit": { "commitId": "{{mergeSha}}" }
        }
        """;

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task CompletePullRequestAsync_HappyPath_ReturnsCompletedWithMergeSha()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        result.Status.ShouldBe("completed");
        result.MergeCommitSha.ShouldBe(MergeSha);
        result.HttpStatus.ShouldBe(200);
        result.ErrorBody.ShouldBeNull();
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompletePullRequestAsync_HitsExpectedUrlAndMethod()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Patch);
        req.RequestUri!.AbsoluteUri.ShouldBe(
            $"https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullRequests/{PrId}?api-version=7.1");
    }

    [Fact]
    public async Task CompletePullRequestAsync_SendsBasicAuthHeader()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler, pat: "my-pat");

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        var auth = handler.Requests[0].Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!))
            .ShouldBe(":my-pat");
    }

    [Fact]
    public async Task CompletePullRequestAsync_SendsCorrectJsonBodyShape()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        var body = handler.RequestBodies[0];
        body.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;

        // Wire shape mirrors ADO's REST contract — camelCase via the
        // explicit [JsonPropertyName] overrides on the request DTO, not
        // the global snake_case policy.
        root.GetProperty("status").GetString().ShouldBe("completed");
        root.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString().ShouldBe(HeadSha);

        var opts = root.GetProperty("completionOptions");
        opts.GetProperty("mergeStrategy").GetString().ShouldBe("noFastForward");
        opts.GetProperty("deleteSourceBranch").GetBoolean().ShouldBeFalse();
        opts.GetProperty("bypassPolicy").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CompletePullRequestAsync_PinsMergeStrategyPerAdrRev4()
    {
        // Explicit regression test for the ADR contract — workflows depend
        // on noFastForward to keep plan branches grafted onto the parent's
        // tip with a recoverable merge commit.
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString()
            .ShouldBe("noFastForward");
    }

    // ─── Routable-failure axes ───────────────────────────────────────────

    [Fact]
    public async Task CompletePullRequestAsync_404_ReturnsNotFound()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, """{"message":"PR vanished"}""");
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        result.Status.ShouldBe("not_found");
        result.MergeCommitSha.ShouldBeNull();
        result.HttpStatus.ShouldBe(404);
        result.ErrorBody.ShouldNotBeNull();
        result.ErrorBody!.ShouldContain("PR vanished");
    }

    [Fact]
    public async Task CompletePullRequestAsync_409_ReturnsStaleHead()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Conflict, """{"message":"head moved"}""");
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        result.Status.ShouldBe("stale_head");
        result.MergeCommitSha.ShouldBeNull();
        result.HttpStatus.ShouldBe(409);
        result.ErrorBody.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompletePullRequestAsync_400_ReturnsNotMergeable()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadRequest, """{"message":"policy blocked"}""");
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        result.Status.ShouldBe("not_mergeable");
        result.MergeCommitSha.ShouldBeNull();
        result.HttpStatus.ShouldBe(400);
        result.ErrorBody.ShouldNotBeNull();
        result.ErrorBody!.ShouldContain("policy blocked");
    }

    // ─── Wire-level failure axes (throw, do not route) ──────────────────

    [Fact]
    public async Task CompletePullRequestAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CompletePullRequestAsync_403_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CompletePullRequestAsync_5xx_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        // 5xx is treated as a real signal — single attempt, no retry.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompletePullRequestAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task CompletePullRequestAsync_NoPat_ThrowsInvalidOperation()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task CompletePullRequestAsync_SuccessWithoutMergeSha_ReturnsCompletedWithNullMergeSha()
    {
        // Defensive — ADO is documented to return lastMergeCommit on a
        // successful complete, but the verb's "missing_merge_commit" error
        // code exists for the case where the wire shape disagrees. The
        // client surfaces null and lets the verb decide.
        var bodyMissingMerge = $$"""
            {
              "pullRequestId": {{PrId}},
              "status": "completed",
              "sourceRefName": "refs/heads/feature/x",
              "targetRefName": "refs/heads/main",
              "lastMergeSourceCommit": { "commitId": "{{HeadSha}}" }
            }
            """;
        var handler = StubHandler.Returns(HttpStatusCode.OK, bodyMissingMerge);
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha);

        result.Status.ShouldBe("completed");
        result.MergeCommitSha.ShouldBeNull();
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("", "p", "r")]
    [InlineData("o", "", "r")]
    [InlineData("o", "p", "")]
    public async Task CompletePullRequestAsync_EmptyIdentifier_ThrowsArgumentException(
        string organization, string project, string repository)
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CompletePullRequestAsync(organization, project, repository, PrId, HeadSha));
    }

    [Fact]
    public async Task CompletePullRequestAsync_NonPositivePrId_ThrowsArgumentOutOfRange()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, 0, HeadSha));
    }

    [Fact]
    public async Task CompletePullRequestAsync_EmptyHeadSha_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, ""));
    }

    [Fact]
    public async Task CompletePullRequestAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, cts.Token));
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
