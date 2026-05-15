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
/// Smoke tests for <c>polyphony pr merge-evidence-ado</c>. Verifies the
/// happy path, already-merged short-circuit, and per-error-code routing.
/// </summary>
public sealed class PrCommandsMergeEvidenceAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrNumber = 42;
    private const string PrUrl = "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42";

    private (PrCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand()
    {
        var ado = new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            ado);
        return (cmd, runner, ado);
    }

    private static PrMergeEvidenceAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeEvidenceAdoResult)!;

    private static AdoPullRequest MakePr(string status)
        => new(PrNumber, "title", "", "refs/heads/evidence/100", "refs/heads/main",
            status, null, "user", DateTime.UtcNow, PrUrl);

    private static AdoPullRequestPollData MakePoll(string state, string headSha = "abc123", string? mergeCommit = null)
        => new()
        {
            Number = PrNumber,
            State = state,
            ReviewDecision = "APPROVED",
            Mergeable = "MERGEABLE",
            HeadRefName = "evidence/100",
            HeadRefOid = headSha,
            BaseRefName = "main",
            Body = "",
            Reviews = [],
            MergeCommit = mergeCommit,
        };

    [Theory]
    [InlineData("", "p", "r", 42, "--organization")]
    [InlineData("o", "", "r", 42, "--project")]
    [InlineData("o", "p", "", 42, "--repository")]
    [InlineData("o", "p", "r", -2147483648, "--pr-number")]
    public async Task MergeEvidenceAdo_MissingRequired_RoutesRequiredInputHalt(
        string organization, string project, string repository, int prNumber, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(organization, project, repository, prNumber: prNumber));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr merge-evidence-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task MergeEvidenceAdo_NegativePrNumber_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: -5));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeEvidenceAdo_NoAdoClient_RoutesAdoFailed()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            ado: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task MergeEvidenceAdo_PrNotFound_RoutesPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = null;
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeEvidenceAdo_AlreadyCompleted_RoutesAlreadyMerged()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("completed");
        ado.PollData = MakePoll("MERGED", mergeCommit: "deadbeef");
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("deadbeef");
        result.ErrorCode.ShouldBeEmpty();
        ado.CompleteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task MergeEvidenceAdo_HappyPath_DispatchesSquashWithDeleteSourceBranch()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.PollData = MakePoll("OPEN");
        ado.CompleteResult = new AdoCompletePullRequestResult("completed", "newcommit", 200, null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.MergeCommit.ShouldBe("newcommit");
        result.ErrorCode.ShouldBeEmpty();
        ado.CompleteCallCount.ShouldBe(1);
        ado.CompleteStrategy.ShouldBe(AdoMergeStrategy.Squash);
        ado.CompleteDeleteSource.ShouldBeTrue();
        ado.CompleteHeadSha.ShouldBe("abc123");
    }

    [Fact]
    public async Task MergeEvidenceAdo_StaleHead_RoutesStaleHead()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.PollData = MakePoll("OPEN");
        ado.CompleteResult = new AdoCompletePullRequestResult("stale_head", null, 409, "src branch advanced");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_head");
        result.Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeEvidenceAdo_NotMergeable_RoutesNotMergeable()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.PollData = MakePoll("OPEN");
        ado.CompleteResult = new AdoCompletePullRequestResult("not_mergeable", null, 400, "policy block");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("not_mergeable");
    }

    [Fact]
    public async Task MergeEvidenceAdo_CompletedWithEmptyShaHasMissingMergeCommit()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.PollData = MakePoll("OPEN");
        ado.CompleteResult = new AdoCompletePullRequestResult("completed", null, 200, null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_merge_commit");
        result.Merged.ShouldBeTrue();
    }

    [Fact]
    public async Task MergeEvidenceAdo_PollUnauthorized_RoutesNoPat()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.ThrowOnPoll = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeEvidenceAdo_NonOpenState_RoutesNotMergeable()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = MakePr("active");
        ado.PollData = MakePoll("CLOSED");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidenceAdo(Org, Project, Repo, prNumber: PrNumber));
        Parse(output).ErrorCode.ShouldBe("not_mergeable");
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoPullRequest? GetPr { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }
        public Exception? ThrowOnPoll { get; set; }
        public Exception? ThrowOnComplete { get; set; }
        public int CompleteCallCount { get; private set; }
        public AdoMergeStrategy CompleteStrategy { get; private set; }
        public bool CompleteDeleteSource { get; private set; }
        public string? CompleteHeadSha { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => Task.FromResult(GetPr);

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title,
            string description, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
        {
            if (ThrowOnPoll is not null) throw ThrowOnPoll;
            return Task.FromResult(PollData);
        }

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            AdoMergeStrategy mergeStrategy, bool deleteSourceBranch,
            CancellationToken ct = default)
        {
            CompleteCallCount++;
            CompleteStrategy = mergeStrategy;
            CompleteDeleteSource = deleteSourceBranch;
            CompleteHeadSha = lastMergeSourceCommitSha;
            if (ThrowOnComplete is not null) throw ThrowOnComplete;
            return Task.FromResult(CompleteResult ?? new AdoCompletePullRequestResult("ado_error", null, 500, null));
        }

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
