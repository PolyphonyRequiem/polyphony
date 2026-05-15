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
/// End-to-end tests for <c>polyphony pr vote-ado</c>. Stubs
/// <see cref="IAdoClient"/> directly (the verb only consumes one method on
/// it) and asserts on the <see cref="PrVoteAdoResult"/> envelope. Always
/// exits 0 — error states surface in <c>error_code</c>.
/// </summary>
public sealed class PrCommandsVoteAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;
    private const string ReviewerId = "11111111-2222-3333-4444-555555555555";

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

    private static PrVoteAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrVoteAdoResult)!;

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task VoteAdo_HappyPath_EmitsSubmittedTrue()
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = true;

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Submitted.ShouldBeTrue();
        result.PrNumber.ShouldBe(PrId);
        result.ReviewerId.ShouldBe(ReviewerId);
        result.Vote.ShouldBe("approve");
        result.VoteValue.ShouldBe(10);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();

        ado.SetVoteCallCount.ShouldBe(1);
        ado.LastOrganization.ShouldBe(Org);
        ado.LastProject.ShouldBe(Project);
        ado.LastRepository.ShouldBe(Repo);
        ado.LastPrId.ShouldBe(PrId);
        ado.LastReviewerId.ShouldBe(ReviewerId);
        ado.LastVote.ShouldBe(10);
    }

    [Theory]
    [InlineData("approve",                  10)]
    [InlineData("approve-with-suggestions",  5)]
    [InlineData("reset",                     0)]
    [InlineData("wait-for-author",          -5)]
    [InlineData("reject",                   -10)]
    public async Task VoteAdo_VoteNameMapping_PassesCorrectIntToAdo(string voteName, int expectedValue)
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = true;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, voteName));
        var result = Parse(output);

        result.Submitted.ShouldBeTrue();
        result.VoteValue.ShouldBe(expectedValue);
        ado.LastVote.ShouldBe(expectedValue);
    }

    // ─── Argument validation (no ADO call expected) ──────────────────────

    [Theory]
    [InlineData("   ",    Project, Repo)]
    public async Task VoteAdo_WhitespaceRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository)
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = true; // would succeed if invoked

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(organization, project, repository, PrId, ReviewerId, "approve"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        ado.SetVoteCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("",   Project, Repo,    "--organization")]
    [InlineData(Org,  "",      Repo,    "--project")]
    [InlineData(Org,  Project, "",      "--repository")]
    public async Task VoteAdo_EmptyRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = true; // would succeed if invoked

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(organization, project, repository, PrId, ReviewerId, "approve"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr vote-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
        ado.SetVoteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task VoteAdo_NonPositivePrNumber_EmitsInvalidArgument()
    {
        var (cmd, ado) = CreateCommand();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, 0, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        ado.SetVoteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task VoteAdo_MissingReviewerId_EmitsInvalidArgument()
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, "", "approve"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr vote-ado");
        envelope.MissingArgs.ShouldContain("--reviewer-id");
        ado.SetVoteCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("APPROVE")]            // case-sensitive — no uppercase
    [InlineData("approved")]            // wrong tense (the ADO state, not the verb name)
    [InlineData("comment")]             // gh's vocabulary, not ours
    [InlineData("yes")]
    public async Task VoteAdo_UnknownVoteName_EmitsInvalidVote(string voteName)
    {
        var (cmd, ado) = CreateCommand();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, voteName));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_vote");
        result.VoteValue.ShouldBe(0);
        ado.SetVoteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task VoteAdo_EmptyVoteName_RoutesRequiredInputHalt()
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, ""));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr vote-ado");
        envelope.MissingArgs.ShouldContain("--vote");
        ado.SetVoteCallCount.ShouldBe(0);
    }

    // ─── ADO error envelopes ─────────────────────────────────────────────

    [Fact]
    public async Task VoteAdo_AdoReturnsFalse_EmitsPrNotFoundErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = false; // mimics IAdoClient returning false on 404

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task VoteAdo_AdoTimeout_EmitsAdoTimeoutErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnSetVote = new TimeoutException("ADO request timed out after 3 attempt(s).");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_timeout");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("timed out");
    }

    [Fact]
    public async Task VoteAdo_NoPat_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnSetVote = new InvalidOperationException(
            "No ADO PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login').");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("no_pat");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Fact]
    public async Task VoteAdo_Unauthorized_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnSetVote = new HttpRequestException(
            "ADO request failed: HTTP 401 Unauthorized",
            inner: null,
            statusCode: HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task VoteAdo_5xx_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnSetVote = new HttpRequestException(
            "ADO request failed: HTTP 502 Bad Gateway",
            inner: null,
            statusCode: HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task VoteAdo_UnexpectedException_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnSetVote = new InvalidDataException("malformed body");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));
        var result = Parse(output);

        result.Submitted.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ado_failed");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("malformed");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task VoteAdo_JsonContract_IsSnakeCase()
    {
        var (cmd, ado) = CreateCommand();
        ado.SetVoteResult = true;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.VoteAdo(Org, Project, Repo, PrId, ReviewerId, "approve"));

        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"reviewer_id\"");
        output.ShouldContain("\"vote\"");
        output.ShouldContain("\"vote_value\"");
        output.ShouldContain("\"submitted\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_url\"");
        // No PascalCase leakage.
        output.ShouldNotContain("\"PrNumber\"");
        output.ShouldNotContain("\"ReviewerId\"");
    }

    // ─── Static helper ──────────────────────────────────────────────────

    [Theory]
    [InlineData("approve",                  true,  10)]
    [InlineData("approve-with-suggestions", true,   5)]
    [InlineData("reset",                    true,   0)]
    [InlineData("wait-for-author",          true,  -5)]
    [InlineData("reject",                   true, -10)]
    [InlineData("APPROVE",                  false,  0)]
    [InlineData("yes",                      false,  0)]
    [InlineData("",                         false,  0)]
    [InlineData(null,                       false,  0)]
    public void TryMapVoteName_NormalizesAcceptedNames(string? name, bool ok, int expected)
    {
        var got = PrCommands.TryMapVoteName(name, out var value);
        got.ShouldBe(ok);
        value.ShouldBe(expected);
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public bool SetVoteResult { get; set; } = true;
        public Exception? ThrowOnSetVote { get; set; }
        public int SetVoteCallCount { get; private set; }
        public string? LastOrganization { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public int LastPrId { get; private set; }
        public string? LastReviewerId { get; private set; }
        public int LastVote { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null,
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
        {
            SetVoteCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repository;
            LastPrId = pullRequestId;
            LastReviewerId = reviewerId;
            LastVote = vote;
            if (ThrowOnSetVote is not null) throw ThrowOnSetVote;
            return Task.FromResult(SetVoteResult);
        }

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            AdoMergeStrategy mergeStrategy, bool deleteSourceBranch,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    
        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> EditPullRequestBodyAsync(
            string organization, string project, string repository,
            int pullRequestId, string body, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> ClosePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBeforeClose, CancellationToken ct = default)
            => throw new NotImplementedException();
}
}
