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
/// Smoke tests for <c>polyphony pr open-evidence-ado</c>. Verifies orphan
/// (apexId == workItem) vs sub-item branch derivation and routing-style envelopes.
/// </summary>
public sealed class PrCommandsOpenEvidenceAdoTests : CommandTestBase
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

    private static void StubBranchesExist(FakeProcessRunner runner, string head, string @base)
    {
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{head}"],
            new ProcessResult(0, $"abc\trefs/heads/{head}\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{@base}"],
            new ProcessResult(0, $"abc\trefs/heads/{@base}\n", ""));
    }

    private static PrOpenEvidenceAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceAdoResult)!;

    private static AdoPullRequest MakePr(int id, string url, string sourceRef, string targetRef, string status = "active")
        => new(id, "title", "", sourceRef, targetRef, status, null, "user", DateTime.UtcNow, url);

    [Theory]
    [InlineData("", "p", "r", "--organization")]
    [InlineData("o", "", "r", "--project")]
    [InlineData("o", "p", "", "--repository")]
    public async Task OpenEvidenceAdo_EmptyIdentifier_RoutesRequiredInputHalt(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(organization, project, repository, workItem: 100));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr open-evidence-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task OpenEvidenceAdo_NegativeApexId_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100, apexId: -1));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("apexId");
    }

    [Fact]
    public async Task OpenEvidenceAdo_NoAdoClient_RoutesAdoFailed()
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
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.HeadBranch.ShouldBe("evidence/100");
        result.BaseBranch.ShouldBe("main");
    }

    [Fact]
    public async Task OpenEvidenceAdo_OrphanApex_HeadIsEvidenceBareIdAndBaseIsMain()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "evidence/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 11,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/11",
            sourceRef: "refs/heads/evidence/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Created.ShouldBeTrue();
        result.HeadBranch.ShouldBe("evidence/100");
        result.BaseBranch.ShouldBe("main");
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
    }

    [Fact]
    public async Task OpenEvidenceAdo_SubItem_HeadIsEvidenceApexDashItemAndBaseIsFeatureBranch()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "evidence/100-200", "feature/100");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 22,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/22",
            sourceRef: "refs/heads/evidence/100-200",
            targetRef: "refs/heads/feature/100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 200, apexId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.HeadBranch.ShouldBe("evidence/100-200");
        result.BaseBranch.ShouldBe("feature/100");
    }

    [Fact]
    public async Task OpenEvidenceAdo_ExistingPrForSameHeadBase_Reuses()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "evidence/100", "main");
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 50,
                url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/50",
                sourceRef: "refs/heads/evidence/100",
                targetRef: "refs/heads/main"),
        };
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100));
        var result = Parse(output);
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(50);
        ado.CreatePrCallCount.ShouldBe(0);
    }

    // AB#3228: completed PR for same source/target is reused on retry
    // (rather than opening a degenerate no-op duplicate).
    [Fact]
    public async Task OpenEvidenceAdo_OnlyCompletedPrForSameHeadBase_Reuses()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "evidence/100", "main");
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 50, url: "https://example.invalid/50",
                sourceRef: "refs/heads/evidence/100",
                targetRef: "refs/heads/main",
                status: "completed"),
        };
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100));
        var result = Parse(output);
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(50);
        ado.CreatePrCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task OpenEvidenceAdo_NoPat_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "evidence/100", "main");
        ado.ThrowOnList = new HttpRequestException("u", null, HttpStatusCode.Unauthorized);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidenceAdo(Org, Project, Repo, workItem: 100));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequest? CreatedPr { get; set; }
        public int CreatePrCallCount { get; private set; }

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
        {
            CreatePrCallCount++;
            return Task.FromResult(CreatedPr);
        }

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
