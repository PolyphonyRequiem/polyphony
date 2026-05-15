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
/// Smoke tests for the ADO branch of <c>polyphony pr check-evidence-floor</c>.
/// The verb is a single body that branches on resolved repo identity; this
/// suite covers the AdoRepo path. Mirrors
/// <see cref="PrCommandsCheckEvidenceFloorTests"/> for the GitHub branch.
/// </summary>
public sealed class PrCommandsCheckEvidenceFloorAdoTests : CommandTestBase
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

    private static PrCheckEvidenceFloorResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;

    [Fact]
    public async Task AdoBranch_HasCommitsAndBody_PassesFloor()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.FloorRead = new AdoEvidenceFloorRead(
            Outcome: AdoEvidenceFloorOutcome.Found,
            CommitCount: 3,
            Body: "real evidence",
            Detail: null);

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        exit.ShouldBe(ExitCodes.Success);
        var r = Parse(output);
        r.Success.ShouldBeTrue();
        r.PassesFloor.ShouldBeTrue();
        r.CommitCount.ShouldBe(3);
        r.BodyLength.ShouldBe("real evidence".Length);
        r.Violations.ShouldBeEmpty();
        ado.FloorCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AdoBranch_PrNotFound_EmitsPrNotFoundEnvelope()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.FloorRead = new AdoEvidenceFloorRead(
            Outcome: AdoEvidenceFloorOutcome.PrNotFound,
            CommitCount: 0, Body: string.Empty, Detail: "PR 9999 not found");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 9999,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("pr_not_found");
        r.ErrorMessage!.ShouldContain("9999");
    }

    [Fact]
    public async Task AdoBranch_AdoFailed_EmitsGhFailedEnvelope()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.FloorRead = new AdoEvidenceFloorRead(
            Outcome: AdoEvidenceFloorOutcome.AdoFailed,
            CommitCount: 0, Body: string.Empty, Detail: "auth missing");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("gh_failed");
        r.ErrorMessage!.ShouldContain("auth");
    }

    [Fact]
    public async Task AdoBranch_AdoClientThrows_EmitsGhFailedEnvelope()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ThrowOnFloor = new HttpRequestException("boom", inner: null, statusCode: HttpStatusCode.InternalServerError);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("gh_failed");
    }

    [Fact]
    public async Task AdoBranch_NoCommits_AndEmptyBody_BothViolations()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.FloorRead = new AdoEvidenceFloorRead(
            Outcome: AdoEvidenceFloorOutcome.Found,
            CommitCount: 0, Body: "  \n  ", Detail: null);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.PassesFloor.ShouldBeFalse();
        r.Violations.Count.ShouldBe(2);
        r.Violations[0].ShouldBe("no_commits");
        r.Violations[1].ShouldBe("empty_body");
    }

    [Fact]
    public async Task AdoPlatformOverride_MissingOrg_EmitsResolverError()
    {
        var (cmd, _, _) = CreateCommand();

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.CheckEvidenceFloor(prNumber: 42,
                platform: "ado", organization: "", project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("gh_failed");
        r.ErrorMessage!.ShouldContain("--organization");
    }

    [Fact]
    public async Task AdoIdentityResolved_FromAdoOriginUrl_DispatchesAdoBranch()
    {
        var (cmd, runner, ado) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, $"https://dev.azure.com/{Org}/{Project}/_git/{Repo}\n", ""));
        ado.FloorRead = new AdoEvidenceFloorRead(
            Outcome: AdoEvidenceFloorOutcome.Found,
            CommitCount: 1, Body: "ok", Detail: null);

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        var r = Parse(output);
        r.PassesFloor.ShouldBeTrue();
        ado.FloorCalls.ShouldBe(1);
        ado.LastFloorOrg.ShouldBe(Org);
        ado.LastFloorProject.ShouldBe(Project);
        ado.LastFloorRepo.ShouldBe(Repo);
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoEvidenceFloorRead? FloorRead { get; set; }
        public Exception? ThrowOnFloor { get; set; }
        public int FloorCalls { get; private set; }
        public string LastFloorOrg { get; private set; } = "";
        public string LastFloorProject { get; private set; } = "";
        public string LastFloorRepo { get; private set; } = "";

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
        {
            FloorCalls++;
            LastFloorOrg = organization;
            LastFloorProject = project;
            LastFloorRepo = repository;
            if (ThrowOnFloor is not null) throw ThrowOnFloor;
            return Task.FromResult(FloorRead ?? new AdoEvidenceFloorRead(
                AdoEvidenceFloorOutcome.AdoFailed, 0, string.Empty, "no fake set"));
        }

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
