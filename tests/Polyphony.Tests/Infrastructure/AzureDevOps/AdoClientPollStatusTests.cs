using System.Net;
using System.Text;
using Polyphony.Infrastructure.AzureDevOps;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// Coverage for <see cref="IAdoClient.GetPullRequestPollDataAsync"/> — the
/// composed call (PR detail + reviewers list) that produces the
/// platform-neutral <see cref="AdoPullRequestPollData"/> consumed by the
/// <c>pr poll-status-ado</c> verb.
///
/// <para>
/// The handler stub serves PR-detail and reviewers responses as a sequence
/// (first detail, then reviewers) so a single happy-path test exercises both
/// legs of the composed call. Failure tests substitute a single response
/// because the failure happens on the first leg.
/// </para>
/// </summary>
public sealed class AdoClientPollStatusTests
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;

    private static AdoTokenResolver TokenResolver(string? token) =>
        new(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]);

    private static AdoClient NewClient(StubHandler handler, string? pat = "real-pat",
        AdoClientPolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new AdoClient(http, TokenResolver(pat), policy ?? AdoClientPolicy.NoRetry);
    }

    private const string ActiveDetailBody = """
        {
          "pullRequestId": 42,
          "description": "PR body text",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "status": "active",
          "mergeStatus": "succeeded",
          "lastMergeSourceCommit": { "commitId": "abc123" }
        }
        """;

    private const string CompletedDetailBody = """
        {
          "pullRequestId": 42,
          "description": "merged PR",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "status": "completed",
          "mergeStatus": "succeeded",
          "closedDate": "2026-05-06T10:00:00Z",
          "lastMergeSourceCommit": { "commitId": "abc123" },
          "lastMergeCommit": { "commitId": "deadbeef" }
        }
        """;

    private const string AbandonedDetailBody = """
        {
          "pullRequestId": 42,
          "description": "abandoned PR",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "status": "abandoned",
          "mergeStatus": "conflicts",
          "lastMergeSourceCommit": { "commitId": "abc123" }
        }
        """;

    private const string ReviewersBody = """
        {
          "count": 2,
          "value": [
            { "displayName": "Alice", "uniqueName": "alice@x", "vote": 10, "isRequired": true },
            { "displayName": "Bob",   "uniqueName": "bob@x",   "vote":  5, "isRequired": true }
          ]
        }
        """;

    private const string EmptyReviewersBody = """{ "count": 0, "value": [] }""";

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestPollDataAsync_HappyPath_ComposesDetailAndReviewers()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, ReviewersBody));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data.ShouldNotBeNull();
        data!.Number.ShouldBe(42);
        data.State.ShouldBe("OPEN");
        data.ReviewDecision.ShouldBe("APPROVED");
        data.Mergeable.ShouldBe("MERGEABLE");
        data.HeadRefName.ShouldBe("feature/x");
        data.HeadRefOid.ShouldBe("abc123");
        data.BaseRefName.ShouldBe("main");
        data.MergedAt.ShouldBeNull();
        data.MergeCommit.ShouldBeNull();
        data.Body.ShouldBe("PR body text");
        data.Reviews.Count.ShouldBe(2);
        data.Reviews[0].Identity.ShouldBe("Alice");
        data.Reviews[0].Vote.ShouldBe("approved");
        data.Reviews[0].SubmittedAt.ShouldBeNull();
        data.Reviews[1].Vote.ShouldBe("approved_with_suggestions");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_HitsExpectedUrls()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, EmptyReviewersBody));
        var client = NewClient(handler);

        await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullrequests/42?api-version=7.1");
        handler.Requests[1].RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullrequests/42/reviewers?api-version=7.1");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_SendsBasicAuthOnBothCalls()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, EmptyReviewersBody));
        var client = NewClient(handler, pat: "my-pat");

        await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        foreach (var req in handler.Requests)
        {
            var auth = req.Headers.Authorization;
            auth.ShouldNotBeNull();
            auth!.Scheme.ShouldBe("Basic");
            Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!))
                .ShouldBe(":my-pat");
        }
    }

    // ─── State mapping ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestPollDataAsync_CompletedStatus_MapsToMerged()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, CompletedDetailBody),
            (HttpStatusCode.OK, EmptyReviewersBody));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.State.ShouldBe("MERGED");
        data.MergedAt.ShouldBe(new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc));
        data.MergeCommit.ShouldBe("deadbeef");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_AbandonedStatus_MapsToClosed()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, AbandonedDetailBody),
            (HttpStatusCode.OK, EmptyReviewersBody));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.State.ShouldBe("CLOSED");
        data.Mergeable.ShouldBe("CONFLICTING");
        data.MergedAt.ShouldBeNull();
        data.MergeCommit.ShouldBeNull();
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_ActiveStatus_MapsToOpen()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, EmptyReviewersBody));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.State.ShouldBe("OPEN");
    }

    // ─── Vote aggregation ────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestPollDataAsync_AnyRejection_AggregatesToRejected()
    {
        const string reviewers = """
            {
              "count": 3,
              "value": [
                { "displayName": "Alice", "vote":  10, "isRequired": true },
                { "displayName": "Bob",   "vote": -10, "isRequired": true },
                { "displayName": "Carol", "vote":   5, "isRequired": true }
              ]
            }
            """;
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, reviewers));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.ReviewDecision.ShouldBe("REJECTED");
        data.Reviews.Count.ShouldBe(3);
        data.Reviews[1].Vote.ShouldBe("rejected");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_AllRequiredApproved_AggregatesToApproved()
    {
        const string reviewers = """
            {
              "count": 2,
              "value": [
                { "displayName": "Alice", "vote": 10, "isRequired": true },
                { "displayName": "Bob",   "vote":  5, "isRequired": true }
              ]
            }
            """;
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, reviewers));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.ReviewDecision.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_RequiredWaitingForAuthor_AggregatesToReviewRequired()
    {
        const string reviewers = """
            {
              "count": 1,
              "value": [
                { "displayName": "Alice", "vote": -5, "isRequired": true }
              ]
            }
            """;
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, reviewers));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.ReviewDecision.ShouldBe("REVIEW_REQUIRED");
        data.Reviews[0].Vote.ShouldBe("waiting_for_author");
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_OnlyOptionalApprovers_AggregatesToReviewRequired()
    {
        const string reviewers = """
            {
              "count": 1,
              "value": [
                { "displayName": "Alice", "vote": 10, "isRequired": false }
              ]
            }
            """;
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, reviewers));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.ReviewDecision.ShouldBe("REVIEW_REQUIRED");
    }

    // ─── Identity fallback ───────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestPollDataAsync_MissingDisplayName_FallsBackToUniqueName()
    {
        const string reviewers = """
            {
              "count": 1,
              "value": [
                { "displayName": null, "uniqueName": "anon@x", "vote": 0, "isRequired": false }
              ]
            }
            """;
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.OK, reviewers));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data!.Reviews[0].Identity.ShouldBe("anon@x");
        data.Reviews[0].Vote.ShouldBe("no_vote");
    }

    // ─── Failure axes ────────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestPollDataAsync_DetailReturns404_ReturnsNullAndSkipsReviewers()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, "");
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data.ShouldBeNull();
        // The reviewers leg must NOT be called when the PR detail returns 404.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_ReviewersReturns404_TreatsAsEmpty()
    {
        var handler = StubHandler.Sequence(
            (HttpStatusCode.OK, ActiveDetailBody),
            (HttpStatusCode.NotFound, ""));
        var client = NewClient(handler);

        var data = await client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId);

        data.ShouldNotBeNull();
        data!.Reviews.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_401_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_403_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_5xxOnDetail_ThrowsHttpRequestExceptionWithoutRetry()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        // 5xx is treated as a real signal — single attempt, no retry.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_TimeoutExhausted_ThrowsTimeoutException()
    {
        var handler = StubHandler.Hangs();
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = NewClient(handler, policy: policy);

        await Should.ThrowAsync<TimeoutException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_NoPat_ThrowsInvalidOperation()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_EmptyOrganization_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.GetPullRequestPollDataAsync("", Project, Repo, PrId));
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_NonPositivePrId_ThrowsArgumentOutOfRange()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, 0));
    }

    [Fact]
    public async Task GetPullRequestPollDataAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.GetPullRequestPollDataAsync(Org, Project, Repo, PrId, cts.Token));
    }

    // ─── MergeStatus mapping ─────────────────────────────────────────────

    [Theory]
    [InlineData("succeeded", "MERGEABLE")]
    [InlineData("conflicts", "CONFLICTING")]
    [InlineData("queued", "UNKNOWN")]
    [InlineData("rejectedByPolicy", "UNKNOWN")]
    [InlineData("notSet", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    public void MapMergeStatus_NormalizesAdoVocabulary(string? input, string expected)
    {
        AdoMergeStatus.Map(input).ShouldBe(expected);
    }

    // ─── Vote mapping ────────────────────────────────────────────────────

    [Theory]
    [InlineData(10, "approved")]
    [InlineData(5, "approved_with_suggestions")]
    [InlineData(0, "no_vote")]
    [InlineData(-5, "waiting_for_author")]
    [InlineData(-10, "rejected")]
    [InlineData(99, "no_vote")]
    public void MapVote_NormalizesAdoVoteEnum(int input, string expected)
    {
        AdoClient.MapVote(input).ShouldBe(expected);
    }

    // ─── Test fake (sequence-aware) ──────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public int RequestCount => Requests.Count;

        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public static StubHandler Returns(HttpStatusCode status, string body) =>
            new((_, _, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        public static StubHandler Sequence(params (HttpStatusCode Status, string Body)[] responses) =>
            new((_, index, _) =>
            {
                // Saturate at the last response so retries inside a single leg
                // re-use that leg's response shape rather than walking off the end.
                var slot = index < responses.Length ? responses[index] : responses[^1];
                return Task.FromResult(new HttpResponseMessage(slot.Status)
                {
                    Content = new StringContent(slot.Body, Encoding.UTF8, "application/json"),
                });
            });

        public static StubHandler Hangs() =>
            new(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        public static StubHandler AlwaysFail() => new((_, _, _) =>
            throw new InvalidOperationException("handler should not be invoked"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return await _respond(request, index, cancellationToken);
        }
    }
}
