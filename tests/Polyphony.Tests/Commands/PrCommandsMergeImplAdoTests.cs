using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Smoke tests for <c>polyphony pr merge-impl-ado</c> — squash-merge the
/// per-item impl PR into its enclosing merge group on Azure DevOps.
/// </summary>
public sealed class PrCommandsMergeImplAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

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

    private static PrMergeImplAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplAdoResult)!;

    private static AdoPullRequest MakePr(int id, string status, string sourceRef, string targetRef)
        => new(id, "title", "", sourceRef, targetRef, status, null, "user", DateTime.UtcNow, "");

    private static AdoPullRequestPollData MakePoll(string state, string headOid, string? mergeCommit = null)
        => new()
        {
            Number = 1,
            State = state,
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "MERGEABLE",
            HeadRefName = "impl/100-200",
            HeadRefOid = headOid,
            BaseRefName = "mg/100_core",
            MergedAt = state == "MERGED" ? DateTime.UtcNow : null,
            MergeCommit = mergeCommit,
            Body = "",
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    [Theory]
    [InlineData("", "p", "r", "--organization")]
    [InlineData("o", "", "r", "--project")]
    [InlineData("o", "p", "", "--repository")]
    public async Task MergeImplAdo_EmptyIdentifier_RoutesRequiredInputHalt(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(organization, project, repository,
                rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr merge-impl-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task MergeImplAdo_InvalidRootId_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 0, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task MergeImplAdo_InvalidMgPath_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "BAD"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task MergeImplAdo_NoAdoClient_RoutesAdoFailed()
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
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.HeadBranch.ShouldBe("impl/100-200");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task MergeImplAdo_PrNotFound_RoutesPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeImplAdo_AlreadyCompletedPr_RoutesAlreadyMerged()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 77, status: "completed",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "MERGED", headOid: "deadbeef", mergeCommit: "merge-sha-7");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("merge-sha-7");
        result.Method.ShouldBe("squash");
        result.DeleteBranch.ShouldBeTrue();
    }

    [Fact]
    public async Task MergeImplAdo_HappyPath_CompletesViaSquashAndDeletesBranch()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 200, Status: "completed", MergeCommitSha: "merge-sha-9", ErrorBody: "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.MergeCommit.ShouldBe("merge-sha-9");
        result.Method.ShouldBe("squash");
        result.DeleteBranch.ShouldBeTrue();
        ado.LastCompleteStrategy.ShouldBe(AdoMergeStrategy.Squash);
        ado.LastCompleteDeleteSourceBranch.ShouldBeTrue();
        ado.LastCompleteSourceSha.ShouldBe("head-sha-1");
    }

    [Fact]
    public async Task MergeImplAdo_StaleHeadFromMatchHeadCommit_RoutesStaleHead()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "live-head-sha");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core",
                matchHeadCommit: "expected-stale-sha"));
        Parse(output).ErrorCode.ShouldBe("stale_head");
    }

    [Fact]
    public async Task MergeImplAdo_StaleHeadFromCompleteCall_RoutesStaleHead()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 409, Status: "stale_head", MergeCommitSha: "", ErrorBody: "branch advanced");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("stale_head");
    }

    [Fact]
    public async Task MergeImplAdo_PrInClosedState_RoutesPrStateInvalid()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "CLOSED", headOid: "x");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_state_invalid");
    }

    [Fact]
    public async Task MergeImplAdo_NoPat_RoutesNoPat()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ThrowOnList = new HttpRequestException("u", null, HttpStatusCode.Unauthorized);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }

        public AdoMergeStrategy? LastCompleteStrategy { get; private set; }
        public bool LastCompleteDeleteSourceBranch { get; private set; }
        public string LastCompleteSourceSha { get; private set; } = "";

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null, CancellationToken ct = default)
        {
            if (ThrowOnList is not null) throw ThrowOnList;
            return Task.FromResult<IReadOnlyList<AdoPullRequest>?>(ListPrs ?? new List<AdoPullRequest>());
        }

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
            => Task.FromResult(PollData);

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
            LastCompleteStrategy = mergeStrategy;
            LastCompleteDeleteSourceBranch = deleteSourceBranch;
            LastCompleteSourceSha = lastMergeSourceCommitSha;
            return Task.FromResult(CompleteResult ?? new AdoCompletePullRequestResult("ado_failed", null, 0, ""));
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
