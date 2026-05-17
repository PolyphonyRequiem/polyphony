using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// ADO-variant smoke tests for <c>polyphony plan detect-state</c>. The full
/// per-state matrix lives in <see cref="PlanCommandsDetectStateTests"/> for
/// the GitHub leg; this file proves the wiring works on the ADO leg —
/// origin parses to an <c>AdoRepo</c>, the verb routes through
/// <see cref="IAdoClient"/> instead of <c>gh</c>, and platform overrides
/// take precedence over origin-URL detection.
/// </summary>
public sealed class PlanCommandsDetectStateAdoTests : CommandTestBase, IDisposable
{
    private const int RootId = 100;
    private const int ChildId = 200;
    private const string ChildPlanBranch = "plan/100-200";
    private const string AdoOriginUrl = "https://dev.azure.com/contoso/CloudVault/_git/repo";

    private readonly string tempCommonDir;
    private readonly string localManifestPath;

    public PlanCommandsDetectStateAdoTests()
    {
        this.tempCommonDir = Path.Combine(Path.GetTempPath(), $"polytest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempCommonDir);
        this.localManifestPath = Path.Combine(this.tempCommonDir, "polyphony", RootId.ToString(), "run.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(this.localManifestPath)!);
    }

    public override void Dispose()
    {
        try { if (Directory.Exists(this.tempCommonDir)) Directory.Delete(this.tempCommonDir, recursive: true); } catch { }
        base.Dispose();
    }

    private (PlanCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, this.tempCommonDir + "\n", ""));
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var git = new GitClient(runner);
        var ado = new FakeAdoClient();
        return (new PlanCommands(walker, Repository, Config, twig, git, new GhClient(runner), ado, new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git), new RepoIdentityResolver(git), new PullRequestReader(new GhClient(runner), ado)), runner, ado);
    }

    private static PlanDetectStateResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDetectStateResult)!;

    private static void StubAdoOrigin(FakeProcessRunner runner)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, AdoOriginUrl + "\n", ""));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    [Fact]
    public async Task DetectState_AdoOrigin_NoPr_NotStarted()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubAdoOrigin(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        ado.ListResult = []; // ADO branch returns empty PR list

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.PlanBranch.ShouldBe(ChildPlanBranch);
        result.PrNumber.ShouldBeNull();
        ado.ListCallCount.ShouldBe(1);
        ado.LastListSourceBranch.ShouldBe(ChildPlanBranch);
        ado.LastOrganization.ShouldBe("contoso");
        ado.LastProject.ShouldBe("CloudVault");
        ado.LastRepository.ShouldBe("repo");
    }

    [Fact]
    public async Task DetectState_AdoOrigin_OpenPr_AwaitingReview()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubAdoOrigin(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        ado.ListResult = [new AdoPullRequest(
            PullRequestId: 42,
            Title: "plan",
            Description: "",
            SourceRefName: $"refs/heads/{ChildPlanBranch}",
            TargetRefName: "refs/heads/feature/100",
            Status: "active",
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: "https://dev.azure.com/contoso/CloudVault/_git/repo/pullrequest/42")];
        ado.PollResult = new AdoPullRequestPollData
        {
            Number = 42,
            State = "OPEN",
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "MERGEABLE",
            HeadRefName = ChildPlanBranch,
            HeadRefOid = "abc123",
            BaseRefName = "feature/100",
            MergedAt = null,
            MergeCommit = null,
            Body = "",
            Reviews = [],
        };

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
        result.PrNumber.ShouldBe(42);
        result.PrState.ShouldBe("OPEN");
        ado.PollCallCount.ShouldBe(1);
        ado.LastPollPrId.ShouldBe(42);
    }

    [Fact]
    public async Task DetectState_PlatformOverride_TakesPrecedenceOverOrigin()
    {
        var (cmd, runner, ado) = CreateCommand();
        // Origin says GH; overrides force ADO.
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "https://github.com/acme/repo.git\n", ""));
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        ado.ListResult = [];

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId,
                platform: "ado",
                organization: "myorg",
                project: "myproj",
                repositoryOverride: "myrepo"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        ado.LastOrganization.ShouldBe("myorg");
        ado.LastProject.ShouldBe("myproj");
        ado.LastRepository.ShouldBe("myrepo");
    }

    // ─── closed_unmerged + recovery via branch deletion ───────────────────

    [Fact]
    public async Task DetectState_AdoAbandonedPrBranchExists_ClosedUnmerged()
    {
        // Parity coverage: ADO's "abandoned" status normalises to "CLOSED"
        // (AdoClient.cs:1295). When the source branch is still on origin,
        // the operator hasn't yet performed the documented remediation —
        // surface the closed_unmerged_gate so the workflow stops instead of
        // silently overwriting an in-flight rejected plan.
        var (cmd, runner, ado) = CreateCommand();
        StubAdoOrigin(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        ado.ListResult = [new AdoPullRequest(
            PullRequestId: 42,
            Title: "plan",
            Description: "",
            SourceRefName: $"refs/heads/{ChildPlanBranch}",
            TargetRefName: "refs/heads/feature/100",
            Status: "abandoned",
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: "https://dev.azure.com/contoso/CloudVault/_git/repo/pullrequest/42")];
        ado.PollResult = new AdoPullRequestPollData
        {
            Number = 42,
            State = "CLOSED",
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "UNKNOWN",
            HeadRefName = ChildPlanBranch,
            HeadRefOid = "abc123",
            BaseRefName = "feature/100",
            MergedAt = null,
            MergeCommit = null,
            Body = "",
            Reviews = [],
        };

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("closed_unmerged");
        result.PrNumber.ShouldBe(42);
        result.PrState.ShouldBe("CLOSED");
        result.BranchExistsOnOrigin.ShouldBeTrue();
    }

    [Fact]
    public async Task DetectState_AdoAbandonedPrBranchDeleted_NotStarted()
    {
        // Reproduces the abandoned-PR dead-end on ADO (cloudvault-service-api
        // apex 62286666, polyphony-findings-2026-05-17.md §1). ADO has no
        // delete-PR operation, so an abandoned PR sticks in PR history
        // forever. Once the operator deletes the plan branch on origin (the
        // remediation that closed_unmerged_gate's prompt asks for), the next
        // detect-state call must classify the historical PR as no longer
        // blocking and route to not_started so the architect can re-plan.
        var (cmd, runner, ado) = CreateCommand();
        StubAdoOrigin(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        ado.ListResult = [new AdoPullRequest(
            PullRequestId: 42,
            Title: "plan",
            Description: "",
            SourceRefName: $"refs/heads/{ChildPlanBranch}",
            TargetRefName: "refs/heads/feature/100",
            Status: "abandoned",
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: "https://dev.azure.com/contoso/CloudVault/_git/repo/pullrequest/42")];
        ado.PollResult = new AdoPullRequestPollData
        {
            Number = 42,
            State = "CLOSED",
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "UNKNOWN",
            HeadRefName = ChildPlanBranch,
            HeadRefOid = "abc123",
            BaseRefName = "feature/100",
            MergedAt = null,
            MergeCommit = null,
            Body = "",
            Reviews = [],
        };

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.BranchExistsOnOrigin.ShouldBeFalse();
        result.PrNumber.ShouldBeNull();
        result.PrState.ShouldBeNull();
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public IReadOnlyList<AdoPullRequest>? ListResult { get; set; }
        public AdoPullRequestPollData? PollResult { get; set; }

        public int ListCallCount { get; private set; }
        public int PollCallCount { get; private set; }
        public string? LastOrganization { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public string? LastListSourceBranch { get; private set; }
        public int LastPollPrId { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null,
            CancellationToken ct = default)
        {
            ListCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repository;
            LastListSourceBranch = sourceBranch;
            return Task.FromResult(ListResult);
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
        {
            PollCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repositoryId;
            LastPollPrId = pullRequestId;
            return Task.FromResult(PollResult);
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
            int pullRequestId, string comment, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
