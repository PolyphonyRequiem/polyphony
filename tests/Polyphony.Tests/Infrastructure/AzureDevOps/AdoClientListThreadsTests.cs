using System.Net;
using System.Text;
using Polyphony.Infrastructure.AzureDevOps;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

/// <summary>
/// HTTP-level coverage for <see cref="IAdoClient.ListPullRequestThreadsAsync"/> —
/// the GET that harvests review-comment threads for the
/// <c>pr get-comments-ado</c> verb. Asserts URL shape, success-path mapping,
/// per-comment + per-thread filter behaviour (deleted / system / empty),
/// and the standard failure envelope mapping (404 → null, 401/5xx → throws,
/// timeout → <see cref="TimeoutException"/>, no PAT →
/// <see cref="InvalidOperationException"/>).
/// </summary>
public sealed class AdoClientListThreadsTests
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

    private const string OkBody = """
        {
          "value": [
            {
              "id": 101,
              "status": "active",
              "isDeleted": false,
              "threadContext": {
                "filePath": "/src/Foo.cs",
                "rightFileStart": { "line": 12, "offset": 1 }
              },
              "comments": [
                {
                  "id": 1,
                  "parentCommentId": 0,
                  "content": "Consider extracting this",
                  "commentType": "text",
                  "isDeleted": false,
                  "publishedDate": "2024-01-01T12:00:00Z",
                  "lastUpdatedDate": "2024-01-01T12:00:00Z",
                  "author": { "displayName": "Reviewer A", "uniqueName": "a@example.com" }
                },
                {
                  "id": 2,
                  "parentCommentId": 1,
                  "content": "Will do",
                  "commentType": "text",
                  "isDeleted": false,
                  "publishedDate": "2024-01-02T12:00:00Z",
                  "lastUpdatedDate": "2024-01-02T12:00:00Z",
                  "author": { "displayName": "Author", "uniqueName": "b@example.com" }
                }
              ]
            },
            {
              "id": 102,
              "status": "fixed",
              "isDeleted": false,
              "comments": [
                {
                  "id": 3,
                  "parentCommentId": 0,
                  "content": "Top-level PR comment",
                  "commentType": "text",
                  "isDeleted": false,
                  "publishedDate": "2024-01-03T12:00:00Z",
                  "lastUpdatedDate": "2024-01-03T12:00:00Z",
                  "author": { "displayName": "Reviewer B" }
                }
              ]
            }
          ]
        }
        """;

    // ─── Happy path + URL shape ──────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestThreadsAsync_HappyPath_HitsExpectedUrlAndProjects()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, OkBody);
        var client = NewClient(handler);

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads.ShouldNotBeNull();
        threads!.Count.ShouldBe(2);

        // URL shape — repository + org + project URL-escaped, api-version 7.1, GET.
        handler.Requests.ShouldHaveSingleItem();
        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Get);
        req.RequestUri!.ToString().ShouldBe(
            $"https://dev.azure.com/{Org}/{Project}/_apis/git/repositories/{Repo}/pullRequests/{PrId}/threads?api-version=7.1");

        // Thread #1 — file/line + two comments + reply chain preserved.
        var t1 = threads[0];
        t1.Id.ShouldBe(101);
        t1.Status.ShouldBe("active");
        t1.IsResolved.ShouldBeFalse();
        t1.FilePath.ShouldBe("/src/Foo.cs");
        t1.Line.ShouldBe(12);
        t1.Comments.Count.ShouldBe(2);
        t1.Comments[0].Id.ShouldBe(1);
        t1.Comments[0].Body.ShouldBe("Consider extracting this");
        t1.Comments[0].Author.ShouldBe("Reviewer A");
        t1.Comments[0].CommentType.ShouldBe("text");
        t1.Comments[0].PublishedAt.ShouldBe(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        t1.Comments[1].ParentCommentId.ShouldBe(1);

        // Thread #2 — top-level (no threadContext) + resolved.
        var t2 = threads[1];
        t2.Id.ShouldBe(102);
        t2.Status.ShouldBe("fixed");
        t2.IsResolved.ShouldBeTrue();
        t2.FilePath.ShouldBeNull();
        t2.Line.ShouldBeNull();
        t2.Comments.ShouldHaveSingleItem();
        t2.Comments[0].Author.ShouldBe("Reviewer B");
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_RepositoryGuid_IsUrlEscaped()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{"value":[]}""");
        var client = NewClient(handler);
        var repoGuid = "00000000-0000-0000-0000-000000000001";

        await client.ListPullRequestThreadsAsync(Org, Project, repoGuid, PrId);

        handler.Requests[0].RequestUri!.ToString().ShouldContain($"/repositories/{repoGuid}/");
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_RepositoryWithSpecialChars_IsUrlEscaped()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{"value":[]}""");
        var client = NewClient(handler);

        await client.ListPullRequestThreadsAsync(Org, Project, "my repo/with chars", PrId);

        var url = handler.Requests[0].RequestUri!.ToString();
        // The slash inside the repo name must be percent-encoded so it does
        // not split the URL path. (Uri normalises %20 back to spaces in
        // RequestUri, so we don't assert on those.)
        url.ShouldContain("%2F");
        url.ShouldNotContain("/repositories/my repo/with chars/");
    }

    // ─── Empty + filter behaviour ────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestThreadsAsync_EmptyValueArray_ReturnsEmptyList()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{"value":[]}""");
        var client = NewClient(handler);

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads.ShouldNotBeNull();
        threads!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_NullValueField_ReturnsEmptyList()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{}""");
        var client = NewClient(handler);

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads.ShouldNotBeNull();
        threads!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_DeletedThread_IsFilteredOut()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": true,
                  "comments": [{ "id": 11, "content": "hi", "commentType": "text", "isDeleted": false }]
                },
                {
                  "id": 2, "status": "active", "isDeleted": false,
                  "comments": [{ "id": 12, "content": "kept", "commentType": "text", "isDeleted": false }]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads!.Count.ShouldBe(1);
        threads[0].Id.ShouldBe(2);
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_DeletedComment_IsFilteredOut()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "comments": [
                    { "id": 11, "content": "deleted", "commentType": "text", "isDeleted": true },
                    { "id": 12, "content": "kept",    "commentType": "text", "isDeleted": false }
                  ]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads!.ShouldHaveSingleItem();
        threads[0].Comments.ShouldHaveSingleItem();
        threads[0].Comments[0].Id.ShouldBe(12);
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_SystemCommentType_IsFilteredOut()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "comments": [
                    { "id": 11, "content": "system noise", "commentType": "system", "isDeleted": false },
                    { "id": 12, "content": "real human",   "commentType": "text",   "isDeleted": false }
                  ]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads!.ShouldHaveSingleItem();
        threads[0].Comments.ShouldHaveSingleItem();
        threads[0].Comments[0].Id.ShouldBe(12);
        threads[0].Comments[0].CommentType.ShouldBe("text");
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_ThreadWithOnlySystemComments_IsDropped()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "comments": [
                    { "id": 11, "content": "noise 1", "commentType": "system", "isDeleted": false },
                    { "id": 12, "content": "noise 2", "commentType": "system", "isDeleted": false }
                  ]
                },
                {
                  "id": 2, "status": "active", "isDeleted": false,
                  "comments": [{ "id": 21, "content": "real", "commentType": "text", "isDeleted": false }]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads!.ShouldHaveSingleItem();
        threads[0].Id.ShouldBe(2);
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_NullThreadContext_TopLevelComment_HasNoFileOrLine()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "threadContext": null,
                  "comments": [{ "id": 11, "content": "x", "commentType": "text", "isDeleted": false }]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads![0].FilePath.ShouldBeNull();
        threads[0].Line.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_NullRightFileStart_NoLine()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "threadContext": { "filePath": "/foo.cs", "rightFileStart": null },
                  "comments": [{ "id": 11, "content": "x", "commentType": "text", "isDeleted": false }]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads![0].FilePath.ShouldBe("/foo.cs");
        threads[0].Line.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_LineZeroOrNegative_IsTreatedAsNull()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "threadContext": { "filePath": "/foo.cs", "rightFileStart": { "line": 0, "offset": 1 } },
                  "comments": [{ "id": 11, "content": "x", "commentType": "text", "isDeleted": false }]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads![0].Line.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_StatusVariants_NormalisedAndResolvedFlag()
    {
        const string body = """
            {
              "value": [
                { "id": 1, "status": "active",   "isDeleted": false, "comments": [{"id":1,"content":"a","commentType":"text"}] },
                { "id": 2, "status": "fixed",    "isDeleted": false, "comments": [{"id":2,"content":"a","commentType":"text"}] },
                { "id": 3, "status": "wontFix",  "isDeleted": false, "comments": [{"id":3,"content":"a","commentType":"text"}] },
                { "id": 4, "status": "closed",   "isDeleted": false, "comments": [{"id":4,"content":"a","commentType":"text"}] },
                { "id": 5, "status": "byDesign", "isDeleted": false, "comments": [{"id":5,"content":"a","commentType":"text"}] },
                { "id": 6, "status": "pending",  "isDeleted": false, "comments": [{"id":6,"content":"a","commentType":"text"}] },
                { "id": 7, "status": "unknown",  "isDeleted": false, "comments": [{"id":7,"content":"a","commentType":"text"}] }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads!.Count.ShouldBe(7);
        threads[0].IsResolved.ShouldBeFalse();
        threads[1].IsResolved.ShouldBeTrue();
        threads[2].IsResolved.ShouldBeTrue();
        threads[3].IsResolved.ShouldBeTrue();
        threads[4].IsResolved.ShouldBeTrue();
        threads[5].IsResolved.ShouldBeFalse();
        threads[6].IsResolved.ShouldBeFalse();
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_AuthorPrefersDisplayName_FallsBackToUniqueName()
    {
        const string body = """
            {
              "value": [
                {
                  "id": 1, "status": "active", "isDeleted": false,
                  "comments": [
                    { "id": 11, "content": "a", "commentType": "text", "isDeleted": false,
                      "author": { "displayName": "", "uniqueName": "fallback@x" } },
                    { "id": 12, "content": "b", "commentType": "text", "isDeleted": false,
                      "author": null }
                  ]
                }
              ]
            }
            """;
        var client = NewClient(StubHandler.Returns(HttpStatusCode.OK, body));

        var threads = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        threads![0].Comments[0].Author.ShouldBe("fallback@x");
        threads[0].Comments[1].Author.ShouldBe(string.Empty);
    }

    // ─── Auth / token resolution ─────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestThreadsAsync_SendsBasicAuthHeader()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, """{"value":[]}""");
        var client = NewClient(handler, pat: "the-token");

        await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        var auth = handler.Requests[0].Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        // `:the-token` base64-encoded.
        auth.Parameter.ShouldBe(Convert.ToBase64String(Encoding.ASCII.GetBytes(":the-token")));
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_NoPat_ThrowsInvalidOperation()
    {
        var client = NewClient(StubHandler.AlwaysFail(), pat: null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId));
    }

    // ─── HTTP failure mapping ────────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestThreadsAsync_404_ReturnsNull()
    {
        var client = NewClient(StubHandler.Returns(HttpStatusCode.NotFound, """{"message":"missing"}"""));

        var result = await client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ListPullRequestThreadsAsync_ErrorStatus_ThrowsHttpRequestException(HttpStatusCode status)
    {
        var client = NewClient(StubHandler.Returns(status, "{}"));

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId));
        ex.StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_HangingResponse_TimesOut()
    {
        var client = NewClient(
            StubHandler.Hangs(),
            policy: new AdoClientPolicy(
                maxAttempts: 1,
                perAttemptTimeout: TimeSpan.FromMilliseconds(50),
                initialBackoff: TimeSpan.Zero));

        await Should.ThrowAsync<TimeoutException>(
            () => client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId));
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_ExternalCancellation_ThrowsOperationCanceled()
    {
        var client = NewClient(
            StubHandler.Hangs(),
            policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.ListPullRequestThreadsAsync(Org, Project, Repo, PrId, cts.Token));
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("",       Project, Repo)]
    [InlineData(Org,      "",      Repo)]
    [InlineData(Org,      Project, "")]
    public async Task ListPullRequestThreadsAsync_EmptyArgument_ThrowsArgumentException(
        string organization, string project, string repository)
    {
        var client = NewClient(StubHandler.AlwaysFail());

        await Should.ThrowAsync<ArgumentException>(
            () => client.ListPullRequestThreadsAsync(organization, project, repository, PrId));
    }

    [Fact]
    public async Task ListPullRequestThreadsAsync_NullArgument_ThrowsArgumentNullException()
    {
        var client = NewClient(StubHandler.AlwaysFail());

        await Should.ThrowAsync<ArgumentNullException>(
            () => client.ListPullRequestThreadsAsync(null!, Project, Repo, PrId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListPullRequestThreadsAsync_NonPositivePrId_ThrowsArgumentOutOfRange(int prId)
    {
        var client = NewClient(StubHandler.AlwaysFail());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.ListPullRequestThreadsAsync(Org, Project, Repo, prId));
    }

    // ─── Test fake (records requests, single response) ───────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _respond(request, cancellationToken);
        }
    }
}
