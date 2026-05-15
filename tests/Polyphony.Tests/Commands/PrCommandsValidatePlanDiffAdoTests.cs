using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Smoke tests for the ADO branch of <c>polyphony pr validate-plan-diff</c>.
/// Companion to <see cref="PrCommandsValidatePlanDiffTests"/> which covers
/// the GitHub branch + helper unit tests. These tests verify the verb
/// dispatches to <see cref="IAdoClient"/> and maps responses correctly into
/// the platform-neutral <see cref="PrValidatePlanDiffResult"/> envelope.
/// </summary>
public sealed class PrCommandsValidatePlanDiffAdoTests : CommandTestBase
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
            new RunLockStore(), new RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            ado);
        return (cmd, runner, ado);
    }

    private static PrValidatePlanDiffResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrValidatePlanDiffResult)!;

    private static AdoPullRequestPollData MakePoll(string body, string headSha, string state = "OPEN")
        => new()
        {
            Number = 42,
            State = state,
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "MERGEABLE",
            HeadRefName = "plan/100",
            HeadRefOid = headSha,
            BaseRefName = "main",
            MergedAt = null,
            MergeCommit = null,
            Body = body,
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    [Fact]
    public async Task AdoBranch_HappyPath_ClassifiesAndReturnsAdoSlug()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.Poll = MakePoll(body: "PR description.", headSha: "abc123");
        ado.Files = new List<AdoPullRequestChangedFile>
        {
            new("plans/plan-100.md", 5, 1, "edit"),
        };

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        exit.ShouldBe(ExitCodes.Success);
        var r = Parse(output);
        r.DiffClassified.ShouldBeTrue();
        r.RepoSlug.ShouldBe($"{Org}/{Project}/{Repo}");
        r.HeadSha.ShouldBe("abc123");
        r.PrState.ShouldBe("OPEN");
        // Self-only edit on the root plan is the clean path.
        r.Severity.ShouldBe("none");
    }

    [Fact]
    public async Task AdoBranch_PrPollReturnsNull_EmitsPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.Poll = null;

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("pr_not_found");
        r.RepoSlug.ShouldBe($"{Org}/{Project}/{Repo}");
        r.DiffClassified.ShouldBeFalse();
    }

    [Fact]
    public async Task AdoBranch_PollHttp404_TreatedAsPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ThrowOnPoll = new HttpRequestException("not found", inner: null,
            statusCode: HttpStatusCode.NotFound);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task AdoBranch_PollAuthFailure_EmitsInternalError()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ThrowOnPoll = new HttpRequestException("unauthorised", inner: null,
            statusCode: HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("internal_error");
        r.Message.ShouldContain("unauthorised");
    }

    [Fact]
    public async Task AdoBranch_FilesNullPayload_EmitsPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.Poll = MakePoll(body: "Body", headSha: "abc");
        ado.Files = null;
        ado.NullFiles = true;

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("pr_not_found");
        r.HeadSha.ShouldBe("abc");
        r.PrState.ShouldBe("OPEN");
    }

    [Fact]
    public async Task AdoBranch_DescendantTouchesParentPlan_EmitsBlocking()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.Poll = MakePoll(body: "Body", headSha: "head1");
        // Descendant plan PR touches the parent plan file with NO
        // requests_parent_change marker — should be blocking.
        ado.Files = new List<AdoPullRequestChangedFile>
        {
            new("plans/plan-200.md", 5, 1, "edit"),  // self
            new("plans/plan-100.md", 1, 0, "edit"),  // parent (forbidden)
        };

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(
                rootId: 100, itemId: 200, prNumber: 42, parentItemId: 100,
                platform: "ado", organization: Org, project: Project, repositoryOverride: Repo));

        var r = Parse(output);
        r.DiffClassified.ShouldBeTrue();
        r.Severity.ShouldBe("blocking");
        r.ParentPlanFiles.ShouldContain("plans/plan-100.md");
    }

    [Fact]
    public async Task AdoIdentityFromOriginUrl_DispatchesAdoBranch()
    {
        var (cmd, runner, ado) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, $"https://dev.azure.com/{Org}/{Project}/_git/{Repo}\n", ""));
        ado.Poll = MakePoll(body: "Body.", headSha: "sha");
        ado.Files = new List<AdoPullRequestChangedFile>
        {
            new("plans/plan-100.md", 1, 0, "edit"),
        };

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42));

        var r = Parse(output);
        r.RepoSlug.ShouldBe($"{Org}/{Project}/{Repo}");
        ado.PollCalls.ShouldBe(1);
        ado.LastPollOrg.ShouldBe(Org);
        ado.LastPollProject.ShouldBe(Project);
        ado.LastPollRepo.ShouldBe(Repo);
    }

    [Fact]
    public async Task AdoPlatform_MissingRepoOverride_EmitsResolverError()
    {
        var (cmd, _, _) = CreateCommand();

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42,
                platform: "ado", organization: Org, project: Project, repositoryOverride: ""));

        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("repo_not_resolved");
        r.Message.ShouldContain("--repository");
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoPullRequestPollData? Poll { get; set; }
        public Exception? ThrowOnPoll { get; set; }
        public IReadOnlyList<AdoPullRequestChangedFile>? Files { get; set; }
        public bool NullFiles { get; set; }
        public Exception? ThrowOnFiles { get; set; }
        public int PollCalls { get; private set; }
        public string LastPollOrg { get; private set; } = "";
        public string LastPollProject { get; private set; } = "";
        public string LastPollRepo { get; private set; } = "";

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
        {
            PollCalls++;
            LastPollOrg = organization;
            LastPollProject = project;
            LastPollRepo = repositoryId;
            if (ThrowOnPoll is not null) throw ThrowOnPoll;
            return Task.FromResult(Poll);
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
        {
            if (ThrowOnFiles is not null) throw ThrowOnFiles;
            if (NullFiles) return Task.FromResult<IReadOnlyList<AdoPullRequestChangedFile>?>(null);
            return Task.FromResult<IReadOnlyList<AdoPullRequestChangedFile>?>(
                Files ?? new List<AdoPullRequestChangedFile>());
        }

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
