using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony reset run</c>. Uses <see cref="FakeProcessRunner"/>
/// to stub twig and git invocations and <see cref="FakeWorkItemCommentClient"/>
/// to stub ADO comment operations.
/// </summary>
public sealed class ResetCommandsTests : CommandTestBase
{
    private ResetCommands CreateCommand(
        FakeProcessRunner runner,
        FakeWorkItemCommentClient? comments = null,
        bool isInputRedirected = true,
        string? readLineResponse = null)
    {
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var statePaths = new PolyphonyStatePaths(git);
        return new ResetCommands(twig, Repository, git, walker, statePaths, comments ?? new FakeWorkItemCommentClient())
        {
            IsInputRedirected = () => isInputRedirected,
            ReadLine = () => readLineResponse,
        };
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenStartsWith("twig", ["sync"], new ProcessResult(0, "{}", ""));

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

    private static void StubTwigConfig(FakeProcessRunner runner, string org = "myorg", string project = "myproj")
    {
        runner.WhenExact("twig", ["config", "organization", "--output", "json"],
            new ProcessResult(0, $"{{\"info\":\"{org}\"}}", ""));
        runner.WhenExact("twig", ["config", "project", "--output", "json"],
            new ProcessResult(0, $"{{\"info\":\"{project}\"}}", ""));
    }

    // ─── Not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task Run_WorkItemNotFound_ReturnsCacheError()
    {
        var runner = new FakeProcessRunner();
        StubSync(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 999));

        exitCode.ShouldBe(ExitCodes.CacheError);
        output.ShouldContain("\"error\"");
        output.ShouldContain("999");
    }

    [Fact]
    public async Task Run_MissingRootId_ReturnsRoutingFailure()
    {
        var runner = new FakeProcessRunner();
        var cmd = CreateCommand(runner);
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Run());

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
    }

    // ─── Dry run ────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_EnumeratesArtifacts_NoMutations()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony:planned; twig")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("planned");
        result.DryRun.ShouldBe(true);
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony:planned");
        result.TagsRemoved.ShouldNotContain("twig");

        // No twig patch should have been called
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    // ─── Needs confirmation ─────────────────────────────────────────────

    [Fact]
    public async Task Run_NoForceNoDryRun_EmitsNeedsConfirmation()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        // When STDIN is redirected (test environment), the verb emits
        // needs_confirmation rather than prompting interactively.
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("needs_confirmation");
    }

    // ─── Interactive cancel ─────────────────────────────────────────────

    [Fact]
    public async Task Run_InteractiveDeclined_EmitsCancelled()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner, isInputRedirected: false, readLineResponse: "n");
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Run_InteractiveConfirmed_Executes()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner, isInputRedirected: false, readLineResponse: "y");
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("executed");
    }

    // ─── Force execution ────────────────────────────────────────────────

    [Fact]
    public async Task Run_Force_StripsTags_EmitsExecuted()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony; twig")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, force: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("executed");
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony");
        result.TagsRemoved.ShouldNotContain("twig");

        // Verify twig patch was invoked
        runner.Invocations.ShouldContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task Run_Force_BypassesPrompt()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, force: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        // --force should skip confirmation and go straight to execution
        result.Action.ShouldBe("executed");
    }

    // ─── Branch enumeration ─────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_EnumeratesBranchesForRoot()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/42\n  impl/42-100\n  mg/42_alpha\n* main\n  plan/42\n  feature/99\n", ""));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/feature/42\n  origin/plan/42-200\n  origin/main\n", ""));
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 42, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.LocalBranchesDeleted.ShouldNotBeNull();
        result.LocalBranchesDeleted!.ShouldContain("feature/42");
        result.LocalBranchesDeleted.ShouldContain("impl/42-100");
        result.LocalBranchesDeleted.ShouldContain("mg/42_alpha");
        result.LocalBranchesDeleted.ShouldContain("plan/42");
        result.LocalBranchesDeleted.ShouldNotContain("main");
        result.LocalBranchesDeleted.ShouldNotContain("feature/99");
        result.RemoteBranchesDeleted.ShouldNotBeNull();
        result.RemoteBranchesDeleted!.ShouldContain("feature/42");
        result.RemoteBranchesDeleted.ShouldContain("plan/42-200");
        result.RemoteBranchesDeleted.ShouldNotContain("main");
    }

    // ─── Descendant tag scrub ───────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_IncludesDescendantTags()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build(),
            new WorkItemBuilder()
                .WithId(101).WithType("Issue").WithTitle("Child")
                .WithState("To Do").WithTags("polyphony; polyphony:planned")
                .WithParentId(100)
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.ItemsPatched.ShouldNotBeNull();
        result.ItemsPatched!.ShouldContain(100);
        result.ItemsPatched.ShouldContain(101);
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony");
        result.TagsRemoved.ShouldContain("polyphony:planned");
    }

    // ─── Comment archiving ──────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_IncludesCommentCount()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var comments = new FakeWorkItemCommentClient();
        comments.CommentsPerItem[100] =
        [
            new AdoWorkItemComment { WorkItemId = 100, CommentId = 1, Text = "note 1", CreatedBy = "bot" },
            new AdoWorkItemComment { WorkItemId = 100, CommentId = 2, Text = "note 2", CreatedBy = "bot" },
        ];

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner, comments);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.CommentsArchived.ShouldBe(2);
    }

    [Fact]
    public async Task Run_Force_ArchivesBeforeClear()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var comments = new FakeWorkItemCommentClient();
        comments.CommentsPerItem[100] =
        [
            new AdoWorkItemComment { WorkItemId = 100, CommentId = 10, Text = "test note", CreatedBy = "bot" },
        ];

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner, comments);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, force: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.CommentsArchived.ShouldBe(1);
        result.ArchivePath.ShouldNotBeNull();

        // Verify archive-before-clear ordering:
        // ListCommentsAsync must happen before DeleteCommentAsync.
        comments.ListCallTimestamps.Count.ShouldBeGreaterThan(0);
        comments.DeleteCallTimestamps.Count.ShouldBeGreaterThan(0);
        comments.ListCallTimestamps.Min().ShouldBeLessThan(comments.DeleteCallTimestamps.Min());

        // Clean up archive file
        if (File.Exists(result.ArchivePath))
            File.Delete(result.ArchivePath);
        var archiveDir = Path.GetDirectoryName(result.ArchivePath);
        if (archiveDir is not null && Directory.Exists(archiveDir) && !Directory.EnumerateFiles(archiveDir).Any())
            Directory.Delete(archiveDir);
    }

    // ─── Worktree removal ───────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_EnumeratesWorktreesForRoot()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubTwigConfig(runner);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0,
                "worktree /main\nbranch refs/heads/main\n\n" +
                "worktree /wt/feature-42\nbranch refs/heads/feature/42\n\n" +
                "worktree /wt/impl-42-100\nbranch refs/heads/impl/42-100\n\n" +
                "worktree /wt/feature-99\nbranch refs/heads/feature/99\n\n", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 42, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.WorktreesRemoved.ShouldNotBeNull();
        result.WorktreesRemoved!.ShouldContain("/wt/feature-42");
        result.WorktreesRemoved.ShouldContain("/wt/impl-42-100");
        result.WorktreesRemoved.ShouldNotContain("/main");
        result.WorktreesRemoved.ShouldNotContain("/wt/feature-99");
    }

    [Fact]
    public async Task Run_Force_RemovesWorktreesBeforeBranches()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/42\n", ""));
        runner.WhenExact("git", ["branch", "-r"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0,
                "worktree /wt/feature-42\nbranch refs/heads/feature/42\n\n", ""));
        runner.WhenStartsWith("git", ["worktree", "remove"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("git", ["branch", "-D"], new ProcessResult(0, "", ""));
        StubTwigConfig(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 42, force: true));

        exitCode.ShouldBe(ExitCodes.Success);

        // Verify worktree removal happened before branch deletion
        var invocations = runner.Invocations.ToList();
        var wtRemoveIdx = invocations.FindIndex(i =>
            i.Executable == "git" && i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "remove");
        var branchDeleteIdx = invocations.FindIndex(i =>
            i.Executable == "git" && i.Arguments.Count >= 2 && i.Arguments[0] == "branch" && i.Arguments[1] == "-D");

        wtRemoveIdx.ShouldBeGreaterThan(-1, "worktree remove should have been invoked");
        branchDeleteIdx.ShouldBeGreaterThan(-1, "branch -D should have been invoked");
        wtRemoveIdx.ShouldBeLessThan(branchDeleteIdx, "worktree remove must happen before branch delete");
    }

    // ─── JSON contract: snake_case ──────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_SnakeCaseFieldNames()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"dry_run\"");
        // Ordinal checks: PascalCase keys must not leak
        output.Contains("\"RootId\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"DryRun\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"TagsRemoved\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"ItemsPatched\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"CommentsArchived\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"WorktreesRemoved\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    // ─── JSON contract: null fields omitted ─────────────────────────────

    [Fact]
    public async Task Run_NullFieldsOmitted_WhenWritingNull()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        // Error should be null → omitted
        output.ShouldNotContain("\"error\"");
    }

    // ─── JSON contract: deserialization round-trip ───────────────────────

    [Fact]
    public async Task Run_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        StubEmptyWorktrees(runner);
        StubTwigConfig(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.RootId.ShouldBe(100);
        result.Action.ShouldBe("planned");
        result.DryRun.ShouldBe(true);
    }

    // ─── Worktree parser unit test ──────────────────────────────────────

    [Fact]
    public void ParseWorktreesForRoot_FiltersCorrectly()
    {
        const string porcelain =
            "worktree /main\nbranch refs/heads/main\n\n" +
            "worktree /wt/feature-42\nbranch refs/heads/feature/42\n\n" +
            "worktree /wt/impl-42-100\nbranch refs/heads/impl/42-100\n\n" +
            "worktree /wt/feature-99\nbranch refs/heads/feature/99\n\n" +
            "worktree /wt/detached\ndetached\n\n";

        var result = ResetCommands.ParseWorktreesForRoot(porcelain, 42);

        result.ShouldContain("/wt/feature-42");
        result.ShouldContain("/wt/impl-42-100");
        result.ShouldNotContain("/main");
        result.ShouldNotContain("/wt/feature-99");
        result.ShouldNotContain("/wt/detached");
    }

    // ─── FakeWorkItemCommentClient ──────────────────────────────────────

    private sealed class FakeWorkItemCommentClient : IWorkItemCommentClient
    {
        public Dictionary<int, IReadOnlyList<AdoWorkItemComment>> CommentsPerItem { get; } = new();
        public List<DateTime> ListCallTimestamps { get; } = [];
        public List<DateTime> DeleteCallTimestamps { get; } = [];
        public List<(int WorkItemId, long CommentId)> DeletedComments { get; } = [];

        public Task<IReadOnlyList<AdoWorkItemComment>> ListCommentsAsync(
            string organization, string project, int workItemId, CancellationToken ct = default)
        {
            ListCallTimestamps.Add(DateTime.UtcNow);
            if (CommentsPerItem.TryGetValue(workItemId, out var comments))
                return Task.FromResult(comments);
            return Task.FromResult<IReadOnlyList<AdoWorkItemComment>>([]);
        }

        public Task<bool> DeleteCommentAsync(
            string organization, string project, int workItemId, long commentId, CancellationToken ct = default)
        {
            DeleteCallTimestamps.Add(DateTime.UtcNow);
            DeletedComments.Add((workItemId, commentId));
            return Task.FromResult(true);
        }
    }
}
