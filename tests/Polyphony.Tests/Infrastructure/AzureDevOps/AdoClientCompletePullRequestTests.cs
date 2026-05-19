using System.Net;
using System.Text;
using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
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

    private static IPolyphonyAuthProvider TokenResolver(string? token) =>
        new PatAuthProvider(new AdoTokenResolver(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]));

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

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString()
            .ShouldBe("noFastForward");
    }

    [Fact]
    public async Task CompletePullRequestAsync_SquashStrategy_WiresSquashAndDeletesSourceBranch()
    {
        // Impl-PR path: squash + source-branch deletion is the GitHub
        // analogue this codepath must produce on ADO.
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.Squash, deleteSourceBranch: true);

        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body!);
        var opts = doc.RootElement.GetProperty("completionOptions");
        opts.GetProperty("mergeStrategy").GetString().ShouldBe("squash");
        opts.GetProperty("deleteSourceBranch").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData(AdoMergeStrategy.Rebase, "rebase")]
    [InlineData(AdoMergeStrategy.RebaseMerge, "rebaseMerge")]
    public async Task CompletePullRequestAsync_OtherStrategies_TranslateToWireString(
        AdoMergeStrategy strategy, string expectedWire)
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody());
        var client = NewClient(handler);

        await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, strategy, deleteSourceBranch: false);

        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString()
            .ShouldBe(expectedWire);
    }

    // ─── Routable-failure axes ───────────────────────────────────────────

    [Fact]
    public async Task CompletePullRequestAsync_404_ReturnsNotFound()
    {
        var handler = StubHandler.Returns(HttpStatusCode.NotFound, """{"message":"PR vanished"}""");
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

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
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CompletePullRequestAsync_403_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        var client = NewClient(handler, pat: "bad-pat");

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CompletePullRequestAsync_5xx_ThrowsHttpRequestException()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        var client = NewClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
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
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task CompletePullRequestAsync_NoPat_ThrowsAdoAuthenticationException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler, pat: null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
        ex.Message.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task CompletePullRequestAsync_SuccessWithoutMergeSha_ReturnsCompletionPending()
    {
        // Defensive — ADO is documented to return lastMergeCommit on a
        // successful complete, but if the wire shape disagrees we must NOT
        // declare the merge landed without a SHA. With NoRetry policy the
        // poll loop is disabled (CompletionMergePollAttempts=0), so the
        // verb falls through to completion_pending immediately.
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

        var result = await client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completion_pending");
        result.MergeCommitSha.ShouldBeNull();
        handler.RequestCount.ShouldBe(1);
    }

    // ─── AB#3227: post-PATCH merge-commit poll loop ──────────────────────

    private const string MergeShaPolled = "abc1234567890def1234567890abcdef12345678";

    private static string BodyMissingMerge() => $$"""
        {
          "pullRequestId": {{PrId}},
          "status": "completed",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "{{HeadSha}}" }
        }
        """;

    /// <summary>
    /// Test policy that exercises the AB#3227 poll loop without sleeping —
    /// 8 poll attempts at zero delay so the loop is instantaneous.
    /// </summary>
    private static AdoClientPolicy PollPolicyZeroDelay(int pollAttempts = 8) => new(
        maxAttempts: 1,
        perAttemptTimeout: TimeSpan.FromSeconds(30),
        initialBackoff: TimeSpan.Zero,
        completionMergePollAttempts: pollAttempts,
        completionMergePollInitialDelay: TimeSpan.Zero);

    [Fact]
    public async Task CompletePullRequestAsync_PatchEmptySha_PollsAndReturnsPopulatedSha()
    {
        // Regression for AB#3227: ADO's complete-PR PATCH returns 200 OK
        // with an empty lastMergeCommit while the CompletionQueueWorker
        // populates the ref asynchronously. The client must poll the PR
        // detail endpoint until the SHA appears (or the budget exhausts).
        var handler = StubHandler.ReturnsInSequence(
            (HttpStatusCode.OK, BodyMissingMerge()),                       // PATCH
            (HttpStatusCode.OK, SuccessBody(mergeSha: MergeShaPolled)));   // First GET
        var client = NewClient(handler, policy: PollPolicyZeroDelay());

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completed");
        result.MergeCommitSha.ShouldBe(MergeShaPolled);
        handler.RequestCount.ShouldBe(2);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Patch);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[1].RequestUri!.AbsoluteUri.ShouldBe(
            $"https://dev.azure.com/myorg/myproj/_apis/git/repositories/myrepo/pullRequests/{PrId}?api-version=7.1");
    }

    [Fact]
    public async Task CompletePullRequestAsync_PollExhausts_ReturnsCompletionPending()
    {
        // Pathological case: ADO never confirms completion. After the
        // budget exhausts, the client surfaces completion_pending so the
        // verb-layer routes the operator to manual recovery rather than
        // pretending the merge landed.
        const int pollAttempts = 3;
        var responses = new (HttpStatusCode, string)[1 + pollAttempts];
        responses[0] = (HttpStatusCode.OK, BodyMissingMerge());
        for (int i = 1; i <= pollAttempts; i++)
        {
            responses[i] = (HttpStatusCode.OK, BodyMissingMerge());
        }
        var handler = StubHandler.ReturnsInSequence(responses);
        var client = NewClient(handler, policy: PollPolicyZeroDelay(pollAttempts: pollAttempts));

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completion_pending");
        result.MergeCommitSha.ShouldBeNull();
        result.ErrorBody.ShouldNotBeNull();
        result.ErrorBody!.ShouldContain($"PR #{PrId}");
        handler.RequestCount.ShouldBe(1 + pollAttempts); // PATCH + N GETs
    }

    [Fact]
    public async Task CompletePullRequestAsync_PollGet404_ReturnsCompletionPending()
    {
        // If the PR vanishes between the PATCH and the poll (operator
        // abandoned it, race with deletion, etc.), don't keep polling a
        // ghost. We can't confirm the merge landed — surface completion
        // pending and let the verb layer route accordingly.
        var handler = StubHandler.ReturnsInSequence(
            (HttpStatusCode.OK, BodyMissingMerge()),    // PATCH
            (HttpStatusCode.NotFound, "{\"message\":\"gone\"}"));  // First poll GET
        var client = NewClient(handler, policy: PollPolicyZeroDelay());

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completion_pending");
        result.MergeCommitSha.ShouldBeNull();
        handler.RequestCount.ShouldBe(2); // PATCH + 1 poll (no further polls after 404)
    }

    [Fact]
    public async Task CompletePullRequestAsync_PollGet401_ThrowsHttpRequestException()
    {
        // Auth failures on the poll are NOT swallowed — the PAT is the
        // same one that succeeded on the PATCH, so a 401 here is a real
        // signal worth bubbling.
        var handler = StubHandler.ReturnsInSequence(
            (HttpStatusCode.OK, BodyMissingMerge()),
            (HttpStatusCode.Unauthorized, ""));
        var client = NewClient(handler, policy: PollPolicyZeroDelay());

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.CompletePullRequestAsync(
                Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CompletePullRequestAsync_PollHonorsCancellation()
    {
        // Cancellation must propagate through the poll loop's Task.Delay
        // and abort cleanly without exhausting the budget.
        var handler = StubHandler.ReturnsInSequence(
            (HttpStatusCode.OK, BodyMissingMerge()),
            (HttpStatusCode.OK, BodyMissingMerge()),
            (HttpStatusCode.OK, BodyMissingMerge()));
        var policy = new AdoClientPolicy(
            maxAttempts: 1,
            perAttemptTimeout: TimeSpan.FromSeconds(30),
            initialBackoff: TimeSpan.Zero,
            completionMergePollAttempts: 8,
            completionMergePollInitialDelay: TimeSpan.FromMilliseconds(200));
        var client = NewClient(handler, policy: policy);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.CompletePullRequestAsync(
                Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false, cts.Token));
    }

    // ─── apex-62286666: don't trust preview-SHA on an active PR ──────────

    /// <summary>
    /// PR detail body where ADO has reported a merge-preview SHA in
    /// <c>lastMergeCommit.commitId</c> but the PR's <c>status</c> is still
    /// <c>"active"</c> (i.e. the merge has NOT landed — ADO has only
    /// previewed what it would land if completion ran). The bug we're
    /// guarding against: prior implementations interpreted populated
    /// <c>lastMergeCommit</c> as "merge landed" and returned the preview
    /// SHA, which (a) never appears on origin and (b) routes downstream
    /// coverage checks to a squash_coverage_mismatch_gate false-positive.
    /// </summary>
    private static string BodyActiveWithPreviewSha(string previewSha = "preview1234567890preview1234567890previe") => $$"""
        {
          "pullRequestId": {{PrId}},
          "status": "active",
          "mergeStatus": "succeeded",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "{{HeadSha}}" },
          "lastMergeCommit": { "commitId": "{{previewSha}}" }
        }
        """;

    [Fact]
    public async Task CompletePullRequestAsync_PatchReturnsActiveWithPreviewSha_DoesNotReportCompleted()
    {
        // Regression for the apex-62286666 dogfood incident: ADO returned
        // 200 OK to the PATCH with status="active" and a populated
        // lastMergeCommit (preview SHA). The verb must NOT report
        // Status="completed" with that SHA — it would be a SHA that
        // never lands on origin. With NoRetry policy the poll loop is
        // disabled, so the verb falls through to completion_pending.
        var handler = StubHandler.Returns(HttpStatusCode.OK, BodyActiveWithPreviewSha());
        var client = NewClient(handler);

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completion_pending");
        result.MergeCommitSha.ShouldBeNull();
        result.ErrorBody.ShouldNotBeNull();
        result.ErrorBody!.ShouldContain($"PR #{PrId}");
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompletePullRequestAsync_PatchActiveThenPollCompletes_ReturnsCompletedWithLandedSha()
    {
        // The typical async-completion case: PATCH returns status="active"
        // with a preview SHA, then the next poll sees status="completed"
        // with the actual landed SHA. The verb must return the LANDED SHA,
        // not the preview SHA from the PATCH response.
        const string landedSha = "landed12345678901234567890landed12345678";
        var handler = StubHandler.ReturnsInSequence(
            (HttpStatusCode.OK, BodyActiveWithPreviewSha()),               // PATCH
            (HttpStatusCode.OK, SuccessBody(mergeSha: landedSha)));         // First poll GET
        var client = NewClient(handler, policy: PollPolicyZeroDelay());

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completed");
        result.MergeCommitSha.ShouldBe(landedSha);
        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task CompletePullRequestAsync_PatchActiveAndPollStaysActive_ReturnsCompletionPending()
    {
        // The bug-class case: ADO accepted the PATCH (200 OK) but the PR
        // remains active for the entire poll budget — most likely a policy
        // block ADO is enforcing silently, or a queue stall. The verb must
        // surface completion_pending rather than reporting a fake merge.
        const int pollAttempts = 3;
        var responses = new (HttpStatusCode, string)[1 + pollAttempts];
        for (int i = 0; i < responses.Length; i++)
        {
            responses[i] = (HttpStatusCode.OK, BodyActiveWithPreviewSha());
        }
        var handler = StubHandler.ReturnsInSequence(responses);
        var client = NewClient(handler, policy: PollPolicyZeroDelay(pollAttempts: pollAttempts));

        var result = await client.CompletePullRequestAsync(
            Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false);

        result.Status.ShouldBe("completion_pending");
        result.MergeCommitSha.ShouldBeNull();
        handler.RequestCount.ShouldBe(1 + pollAttempts);
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
            () => client.CompletePullRequestAsync(organization, project, repository, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
    }

    [Fact]
    public async Task CompletePullRequestAsync_NonPositivePrId_ThrowsArgumentOutOfRange()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, 0, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
    }

    [Fact]
    public async Task CompletePullRequestAsync_EmptyHeadSha_ThrowsArgumentException()
    {
        var handler = StubHandler.AlwaysFail();
        var client = NewClient(handler);

        await Should.ThrowAsync<ArgumentException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, "", AdoMergeStrategy.NoFastForward, deleteSourceBranch: false));
    }

    [Fact]
    public async Task CompletePullRequestAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        var client = NewClient(handler, policy: AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.CompletePullRequestAsync(Org, Project, Repo, PrId, HeadSha, AdoMergeStrategy.NoFastForward, deleteSourceBranch: false, cts.Token));
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

        /// <summary>
        /// Replays the supplied (status, body) responses in order, one per
        /// incoming request. Throws <see cref="InvalidOperationException"/>
        /// if more requests arrive than responses queued — the test must
        /// account for every HTTP interaction.
        /// </summary>
        public static StubHandler ReturnsInSequence(params (HttpStatusCode Status, string Body)[] responses)
        {
            var queue = new Queue<(HttpStatusCode Status, string Body)>(responses);
            return new((_, _) =>
            {
                if (queue.Count == 0)
                {
                    throw new InvalidOperationException(
                        "StubHandler.ReturnsInSequence: more requests arrived than queued responses.");
                }
                var (status, body) = queue.Dequeue();
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            });
        }

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
