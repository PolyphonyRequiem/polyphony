using System.Text.Json;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Reset;
using Polyphony.Routing;
using Polyphony.Tests.Commands;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Reset;

/// <summary>
/// Tests for <see cref="ResetPlanner"/>. Stubs <see cref="IAdoClient"/>,
/// <see cref="IGitClient"/> (via <see cref="FakeProcessRunner"/>), and the
/// twig cache to verify artifact enumeration is complete and deterministic.
/// </summary>
public sealed class ResetPlannerTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

    private ResetPlanner CreatePlanner(FakeProcessRunner runner, FakeAdoClient? ado = null)
    {
        var git = new GitClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var statePaths = new PolyphonyStatePaths(git);
        return new ResetPlanner(Repository, git, walker, statePaths, ado ?? new FakeAdoClient());
    }

    private static void StubGitCommonDir(FakeProcessRunner runner, string dir)
        => runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, dir, ""));

    private static void StubEmptyBranches(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["branch", "--list"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["branch", "-r"], new ProcessResult(0, "", ""));
    }

    private static void StubEmptyWorktrees(FakeProcessRunner runner)
        => runner.WhenExact("git", ["worktree", "list", "--porcelain"], new ProcessResult(0, "", ""));

    // ─── Tag enumeration ────────────────────────────────────────────────

    [Fact]
    public async Task Plan_EnumeratesTags_FromRootAndDescendants()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony:planned; twig")
                .Build(),
            new WorkItemBuilder()
                .WithId(101).WithType("Issue").WithTitle("Child")
                .WithState("To Do").WithTags("polyphony; polyphony:planned")
                .WithParentId(100)
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.RootId.ShouldBe(100);
        plan.AffectedItemIds.ShouldContain(100);
        plan.AffectedItemIds.ShouldContain(101);
        plan.MatchingTags.ShouldContain("polyphony:root");
        plan.MatchingTags.ShouldContain("polyphony:planned");
        plan.MatchingTags.ShouldContain("polyphony");
        plan.MatchingTags.ShouldNotContain("twig");

        plan.TagRemovals.Length.ShouldBe(2);
        var rootRemoval = plan.TagRemovals.First(r => r.ItemId == 100);
        rootRemoval.Tags.ShouldContain("polyphony:root");
        rootRemoval.Tags.ShouldContain("polyphony:planned");
        rootRemoval.Tags.ShouldNotContain("twig");

        var childRemoval = plan.TagRemovals.First(r => r.ItemId == 101);
        childRemoval.Tags.ShouldContain("polyphony");
        childRemoval.Tags.ShouldContain("polyphony:planned");
    }

    [Fact]
    public async Task Plan_NoTags_EmptyTagRemovals()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("twig")
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.TagRemovals.ShouldBeEmpty();
        plan.MatchingTags.ShouldBeEmpty();
        plan.AffectedItemIds.ShouldBeEmpty();
    }

    // ─── State dir enumeration ──────────────────────────────────────────

    [Fact]
    public async Task Plan_EnumeratesStateDir()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.StateDir.ShouldNotBeNull();
        plan.StateDir.ShouldContain("polyphony");
        plan.StateDir.ShouldContain("100");
        // Directory doesn't exist in test, so StateDirExists should be false.
        plan.StateDirExists.ShouldBeFalse();
    }

    [Fact]
    public async Task Plan_NoGitRepo_StateDirIsNull()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        // No git common dir stub → GetCommonDirAsync returns null.
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(1, "", "fatal: not a git repository"));
        StubEmptyBranches(runner);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"], new ProcessResult(1, "", ""));

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.StateDir.ShouldBeNull();
        plan.StateDirExists.ShouldBeFalse();
    }

    // ─── Branch enumeration ─────────────────────────────────────────────

    [Fact]
    public async Task Plan_EnumeratesBranchesByRoot()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/42\n  impl/42-100\n  mg/42_alpha\n* main\n  plan/42\n  feature/99\n  evidence/42-200\n", ""));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/feature/42\n  origin/plan/42-200\n  origin/main\n  origin/impl/42-300\n", ""));
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(42);

        plan.LocalBranches.ShouldContain("feature/42");
        plan.LocalBranches.ShouldContain("impl/42-100");
        plan.LocalBranches.ShouldContain("mg/42_alpha");
        plan.LocalBranches.ShouldContain("plan/42");
        plan.LocalBranches.ShouldContain("evidence/42-200");
        plan.LocalBranches.ShouldNotContain("main");
        plan.LocalBranches.ShouldNotContain("feature/99");

        plan.RemoteBranches.ShouldContain("feature/42");
        plan.RemoteBranches.ShouldContain("plan/42-200");
        plan.RemoteBranches.ShouldContain("impl/42-300");
        plan.RemoteBranches.ShouldNotContain("main");
    }

    [Fact]
    public async Task Plan_SdlcApexBranch_Included()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  sdlc/apex/42\n  sdlc/apex/99\n", ""));
        runner.WhenExact("git", ["branch", "-r"], new ProcessResult(0, "", ""));
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(42);

        plan.LocalBranches.ShouldContain("sdlc/apex/42");
        plan.LocalBranches.ShouldNotContain("sdlc/apex/99");
    }

    // ─── Worktree enumeration ───────────────────────────────────────────

    [Fact]
    public async Task Plan_EnumeratesWorktrees_UnderApexRoot()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        // Use a proper non-bare git layout: commonDir = <repo>/.git
        var repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "test-repo"));
        var commonDir = Path.Combine(repoDir, ".git");
        // RunsRootResolver expects: parent of .git's parent = runs root
        // For non-bare: runsRoot = <parent>/<basename>-runs = <temp>/test-repo-runs
        var expectedRunsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "test-repo-runs"));
        var apexWorktree = Path.Combine(expectedRunsRoot, "apex-42", "feature-42");
        var otherWorktree = Path.Combine(expectedRunsRoot, "apex-99", "feature-99");
        var mainWorktree = repoDir;

        var porcelainOutput = $"worktree {mainWorktree}\nHEAD abc123\nbranch refs/heads/main\n\n"
            + $"worktree {apexWorktree}\nHEAD def456\nbranch refs/heads/feature/42\n\n"
            + $"worktree {otherWorktree}\nHEAD ghi789\nbranch refs/heads/feature/99\n\n";

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, commonDir);
        StubEmptyBranches(runner);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelainOutput, ""));

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(42);

        plan.Worktrees.ShouldContain(apexWorktree);
        plan.Worktrees.ShouldNotContain(otherWorktree);
        plan.Worktrees.ShouldNotContain(mainWorktree);
    }

    [Fact]
    public async Task Plan_NoWorktrees_EmptyArray()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.Worktrees.ShouldBeEmpty();
    }

    // ─── Comment enumeration ────────────────────────────────────────────

    [Fact]
    public async Task Plan_WithAdoContext_EnumeratesPolyphonyComments()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var ado = new FakeAdoClient();
        // PR whose source branch belongs to root 42.
        ado.PullRequests = [
            new AdoPullRequest(1, "Plan PR", "", "refs/heads/plan/42", "refs/heads/feature/42", "active", null, "bot", DateTime.UtcNow, "https://dev.azure.com"),
            new AdoPullRequest(2, "Other PR", "", "refs/heads/feature/99", "refs/heads/main", "active", null, "bot", DateTime.UtcNow, "https://dev.azure.com"),
        ];
        // Threads on PR 1: one polyphony advisory (closed + top-level), one human review (active + file-anchored).
        ado.ThreadsByPr[1] = [
            new AdoPullRequestThread
            {
                Id = 10, Status = "closed", IsResolved = true, FilePath = null,
                Comments = [new AdoPullRequestComment { Id = 100, ParentCommentId = 0, Author = "bot", Body = "Advisory", CommentType = "text" }],
            },
            new AdoPullRequestThread
            {
                Id = 11, Status = "active", IsResolved = false, FilePath = "src/Foo.cs",
                Comments = [new AdoPullRequestComment { Id = 101, ParentCommentId = 0, Author = "human", Body = "Review", CommentType = "text" }],
            },
        ];

        var planner = CreatePlanner(runner, ado);
        var plan = await planner.PlanAsync(42, Org, Project, Repo);

        plan.Comments.ShouldNotBeNull();
        plan.Comments!.Length.ShouldBe(1);
        plan.Comments[0].PullRequestId.ShouldBe(1);
        plan.Comments[0].ThreadId.ShouldBe(10);
    }

    [Fact]
    public async Task Plan_WithoutAdoContext_CommentsNull()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        plan.Comments.ShouldBeNull();
    }

    [Fact]
    public async Task Plan_AdoFailure_CommentsGracefullyEmpty()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var ado = new FakeAdoClient { ThrowOnListPrs = new HttpRequestException("PAT rejected") };

        var planner = CreatePlanner(runner, ado);
        var plan = await planner.PlanAsync(42, Org, Project, Repo);

        // Should not throw; comments should be null (empty → null).
        plan.Comments.ShouldBeNull();
    }

    // ─── Zero mutation ──────────────────────────────────────────────────

    [Fact]
    public async Task Plan_PerformsZeroMutations()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony:planned")
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/100\n", ""));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/feature/100\n", ""));
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        // Verify no mutation commands were invoked.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git" && i.Arguments.Count > 0 && i.Arguments[0] == "branch" && i.Arguments.Contains("-D"));
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git" && i.Arguments.Count > 0 && i.Arguments[0] == "push" && i.Arguments.Contains("--delete"));

        // But the plan should still have populated data.
        plan.TagRemovals.Length.ShouldBe(1);
        plan.LocalBranches.ShouldContain("feature/100");
        plan.RemoteBranches.ShouldContain("feature/100");
    }

    // ─── JSON serialization ─────────────────────────────────────────────

    [Fact]
    public async Task Plan_SerializesViaPolyphonyJsonContext()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);

        var json = JsonSerializer.Serialize(plan, PolyphonyJsonContext.Default.ResetPlan);
        json.ShouldNotBeNullOrEmpty();

        var deserialized = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetPlan);
        deserialized.ShouldNotBeNull();
        deserialized!.RootId.ShouldBe(100);
        deserialized.MatchingTags.ShouldContain("polyphony:root");
    }

    [Fact]
    public async Task Plan_SnakeCaseFieldNames()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);
        var json = JsonSerializer.Serialize(plan, PolyphonyJsonContext.Default.ResetPlan);

        json.ShouldContain("\"root_id\"");
        json.ShouldContain("\"tag_removals\"");
        json.ShouldContain("\"matching_tags\"");
        json.ShouldContain("\"affected_item_ids\"");
        json.ShouldContain("\"state_dir\"");
        json.ShouldContain("\"local_branches\"");
        json.ShouldContain("\"remote_branches\"");
        json.ShouldContain("\"worktrees\"");

        // PascalCase keys must NOT leak.
        json.Contains("\"RootId\"", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("\"TagRemovals\"", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("\"MatchingTags\"", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("\"AffectedItemIds\"", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("\"LocalBranches\"", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("\"RemoteBranches\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task Plan_NullFieldsOmitted()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan = await planner.PlanAsync(100);
        var json = JsonSerializer.Serialize(plan, PolyphonyJsonContext.Default.ResetPlan);

        // Comments should be null → omitted.
        json.ShouldNotContain("\"comments\"");
    }

    // ─── Determinism ────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_IsDeterministic_MultipleCallsSameResult()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony")
                .Build());

        var runner = new FakeProcessRunner();
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/100\n  impl/100-200\n", ""));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/feature/100\n", ""));
        StubEmptyWorktrees(runner);

        var planner = CreatePlanner(runner);
        var plan1 = await planner.PlanAsync(100);
        var plan2 = await planner.PlanAsync(100);

        var json1 = JsonSerializer.Serialize(plan1, PolyphonyJsonContext.Default.ResetPlan);
        var json2 = JsonSerializer.Serialize(plan2, PolyphonyJsonContext.Default.ResetPlan);
        json1.ShouldBe(json2);
    }

    // ─── FakeAdoClient ──────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public IReadOnlyList<AdoPullRequest>? PullRequests { get; set; }
        public Dictionary<int, IReadOnlyList<AdoPullRequestThread>> ThreadsByPr { get; } = [];
        public Exception? ThrowOnListPrs { get; set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            CancellationToken ct = default)
        {
            if (ThrowOnListPrs is not null) throw ThrowOnListPrs;
            return Task.FromResult(PullRequests);
        }

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository, int pullRequestId,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title, string description,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId, int pullRequestId,
            CancellationToken ct = default)
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
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository, int pullRequestId,
            CancellationToken ct = default)
        {
            ThreadsByPr.TryGetValue(pullRequestId, out var threads);
            return Task.FromResult<IReadOnlyList<AdoPullRequestThread>?>(threads);
        }
    }
}
