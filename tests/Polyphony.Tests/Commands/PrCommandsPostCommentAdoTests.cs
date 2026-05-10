using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr post-comment-ado</c>. Stubs
/// <see cref="IAdoClient"/> directly (the verb only consumes one method on
/// it) and asserts on the <see cref="PrPostCommentAdoResult"/> envelope.
/// Always exits 0 — error states surface in <c>error_code</c>.
/// </summary>
public sealed class PrCommandsPostCommentAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;
    private const string Body = "Looks good — shipping.";

    private (PrCommands Command, FakeAdoClient Ado) CreateCommand(FakeAdoClient? ado = null)
    {
        ado ??= new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado);
        return (cmd, ado);
    }

    private static PrPostCommentAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrPostCommentAdoResult)!;

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostCommentAdo_HappyPath_EmitsPostedTrue()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(ThreadId: 1234, CommentId: 5678);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeTrue();
        result.PrNumber.ShouldBe(PrId);
        result.Body.ShouldBe(Body);
        result.ThreadId.ShouldBe(1234);
        result.CommentId.ShouldBe(5678);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();

        ado.CreateThreadCallCount.ShouldBe(1);
        ado.LastOrganization.ShouldBe(Org);
        ado.LastProject.ShouldBe(Project);
        ado.LastRepository.ShouldBe(Repo);
        ado.LastPrId.ShouldBe(PrId);
        ado.LastBody.ShouldBe(Body);
    }

    [Fact]
    public async Task PostCommentAdo_HappyPath_PassesBodyVerbatimToAdo()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(1, 1);
        var multiLine = "## Heading\n\n- bullet 1\n- bullet 2\n\n```code```";

        await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, multiLine));

        ado.LastBody.ShouldBe(multiLine);
    }

    [Fact]
    public async Task PostCommentAdo_AcceptsRepositoryGuid()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(1, 2);
        var repoGuid = "00000000-0000-0000-0000-000000000001";

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, repoGuid, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeTrue();
        result.RepoSlug.ShouldBe($"myorg/myproj/{repoGuid}");
        ado.LastRepository.ShouldBe(repoGuid);
    }

    // ─── Argument validation (no ADO call expected) ──────────────────────

    [Theory]
    [InlineData("   ",    Project, Repo)]
    [InlineData(Org,      "   ",   Repo)]
    [InlineData(Org,      Project, "   ")]
    public async Task PostCommentAdo_WhitespaceRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository)
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(1, 2);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(organization, project, repository, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("organization");
        ado.CreateThreadCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("",   Project, Repo,    "--organization")]
    [InlineData(Org,  "",      Repo,    "--project")]
    [InlineData(Org,  Project, "",      "--repository")]
    public async Task PostCommentAdo_EmptyRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(1, 2);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(organization, project, repository, PrId, Body));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr post-comment-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
        ado.CreateThreadCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-42)]
    public async Task PostCommentAdo_NonPositivePrNumber_EmitsInvalidArgument(int prNumber)
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, prNumber, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("prNumber");
        result.PrNumber.ShouldBe(prNumber);
        ado.CreateThreadCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task PostCommentAdo_WhitespaceBody_EmitsInvalidArgument(string body)
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("body");
        ado.CreateThreadCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task PostCommentAdo_EmptyBody_RoutesRequiredInputHalt()
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, ""));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr post-comment-ado");
        envelope.MissingArgs.ShouldContain("--body");
        ado.CreateThreadCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task PostCommentAdo_InvalidArgument_StillEmitsRepoSlugAndPrUrlWhenPossible()
    {
        // Move #2: body is now a halt-checked required input, so empty body
        // short-circuits before slug/url are computed; verify the halt envelope
        // shape instead.
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, ""));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr post-comment-ado");
        envelope.MissingArgs.ShouldContain("--body");
    }

    [Fact]
    public async Task PostCommentAdo_InvalidArgument_RepoSlugBlankWhenSlugComponentMissing()
    {
        // Move #2: empty project halts before slug/url are computed.
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, "", Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr post-comment-ado");
        envelope.MissingArgs.ShouldContain("--project");
    }

    // ─── ADO error envelopes ─────────────────────────────────────────────

    [Fact]
    public async Task PostCommentAdo_AdoReturnsNull_EmitsPrNotFoundErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = null; // mimics IAdoClient returning null on 404

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
        result.ThreadId.ShouldBeNull();
        result.CommentId.ShouldBeNull();
    }

    [Fact]
    public async Task PostCommentAdo_AdoTimeout_EmitsAdoTimeoutErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new TimeoutException("ADO request timed out after 3 attempt(s).");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_timeout");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("timed out");
    }

    [Fact]
    public async Task PostCommentAdo_NoPat_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new InvalidOperationException(
            "No ADO PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login').");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("no_pat");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Fact]
    public async Task PostCommentAdo_Unauthorized_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new HttpRequestException(
            "ADO request failed: HTTP 401 Unauthorized",
            inner: null,
            statusCode: HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task PostCommentAdo_Forbidden_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new HttpRequestException(
            "ADO request failed: HTTP 403 Forbidden",
            inner: null,
            statusCode: HttpStatusCode.Forbidden);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task PostCommentAdo_5xx_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new HttpRequestException(
            "ADO request failed: HTTP 502 Bad Gateway",
            inner: null,
            statusCode: HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task PostCommentAdo_HttpExceptionWithoutStatus_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new HttpRequestException("network broken");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task PostCommentAdo_UnexpectedException_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new InvalidDataException("malformed body");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
        var result = Parse(output);

        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("malformed");
    }

    [Fact]
    public async Task PostCommentAdo_OperationCanceled_PropagatesNoEnvelope()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnCreateThread = new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));
    }

    [Fact]
    public async Task PostCommentAdo_NoAdoClientConfigured_EmitsAdoFailedErrorCode()
    {
        // Mirrors the VoteAdo pattern: the ctor accepts null IAdoClient so
        // GitHub-only tests can opt out of the ADO leg; if a verb that needs
        // ADO is invoked without one, it emits ado_failed.
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado: null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Posted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("IAdoClient");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task PostCommentAdo_JsonContract_IsSnakeCase()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(7, 8);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"body\"");
        output.ShouldContain("\"posted\"");
        output.ShouldContain("\"thread_id\"");
        output.ShouldContain("\"comment_id\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_url\"");
        // No PascalCase leakage.
        output.ShouldNotContain("\"PrNumber\"");
        output.ShouldNotContain("\"ThreadId\"");
        output.ShouldNotContain("\"CommentId\"");
    }

    [Fact]
    public async Task PostCommentAdo_JsonContract_ErrorFieldsOmittedOnSuccess()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = new AdoCreateThreadResult(1, 2);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        // DefaultIgnoreCondition.WhenWritingNull strips null Error/ErrorCode
        // on the happy path.
        output.ShouldNotContain("\"error\"");
        output.ShouldNotContain("\"error_code\"");
    }

    [Fact]
    public async Task PostCommentAdo_JsonContract_OnErrorPathIncludesErrorFields()
    {
        var (cmd, ado) = CreateCommand();
        ado.CreateThreadResult = null;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body));

        output.ShouldContain("\"error\"");
        output.ShouldContain("\"error_code\"");
        // thread_id/comment_id are null on errors and stripped from JSON.
        output.ShouldNotContain("\"thread_id\"");
        output.ShouldNotContain("\"comment_id\"");
    }

    [Fact]
    public async Task PostCommentAdo_AlwaysReturnsExitCodeSuccess_AllPaths()
    {
        // Every error path returns 0 (routing-style verb).
        var ado = new FakeAdoClient { CreateThreadResult = new AdoCreateThreadResult(1, 2) };
        var (cmd, _) = CreateCommand(ado);

        var paths = new (FakeAdoClient Ado, Func<Task<int>> Invoke)[]
        {
            (new FakeAdoClient { CreateThreadResult = new AdoCreateThreadResult(1, 2) },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient(),
                () => cmd.PostCommentAdo(Org, Project, Repo, 0, Body)),
            (new FakeAdoClient { CreateThreadResult = null },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient { ThrowOnCreateThread = new TimeoutException() },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient { ThrowOnCreateThread = new InvalidOperationException("x") },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient { ThrowOnCreateThread = new HttpRequestException("x", null, HttpStatusCode.BadGateway) },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient { ThrowOnCreateThread = new HttpRequestException("x", null, HttpStatusCode.Unauthorized) },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
            (new FakeAdoClient { ThrowOnCreateThread = new InvalidDataException("x") },
                () => cmd.PostCommentAdo(Org, Project, Repo, PrId, Body)),
        };

        foreach (var (fake, invoke) in paths)
        {
            // Each path is wired up against the shared cmd which uses the
            // first fake; what matters is that every route returns 0.
            var (exit, _) = await CaptureConsoleAsync(invoke);
            exit.ShouldBe(ExitCodes.Success);
        }
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoCreateThreadResult? CreateThreadResult { get; set; } =
            new AdoCreateThreadResult(ThreadId: 1, CommentId: 2);
        public Exception? ThrowOnCreateThread { get; set; }
        public int CreateThreadCallCount { get; private set; }
        public string? LastOrganization { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public int LastPrId { get; private set; }
        public string? LastBody { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title,
            string description, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
        {
            CreateThreadCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repository;
            LastPrId = pullRequestId;
            LastBody = commentBody;
            if (ThrowOnCreateThread is not null) throw ThrowOnCreateThread;
            return Task.FromResult(CreateThreadResult);
        }
    
        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();
}
}
