using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony branch ensure-plan</c>. Idempotent plan branch
/// materialization with auto-derived base from <c>--root-id</c>,
/// <c>--item-id</c>, and (optionally) <c>--parent-item-id</c>. Three
/// shapes: root plan, child of root plan, deeper descendant — all
/// captured by the same flat <c>plan/{root}-{item}</c> grammar with the
/// hierarchy living in the PR base.
/// </summary>
public sealed class BranchCommandsEnsurePlanTests : CommandTestBase
{
    private static (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>())
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new BranchCommands(twig, walker, repo, validator, git, gh, config), runner);
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubLocalBranchExists(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? "abc123\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

    // ─── Input validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, 0)]            // root id zero
    [InlineData(-1, 100, 0)]           // root id negative
    [InlineData(100, 0, 0)]            // item id zero
    [InlineData(100, -5, 0)]           // item id negative
    public async Task EnsurePlan_InvalidIds_ReturnsConfigError(int rootId, int itemId, int parentItemId)
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: rootId, itemId: itemId, parentItemId: parentItemId));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task EnsurePlan_RootPlanWithParentItemId_ReturnsConfigError()
    {
        // Root plan has no parent — explicit parent arg is rejected to keep
        // input semantics unambiguous.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100, parentItemId: 50));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("--parent-item-id must not be provided");
    }

    [Fact]
    public async Task EnsurePlan_ParentEqualsItem_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: 50));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("must not equal --item-id");
    }

    [Fact]
    public async Task EnsurePlan_ParentEqualsRoot_ReturnsConfigError()
    {
        // parent == root means "parent is the root plan", which the verb
        // expresses by OMITTING --parent-item-id. Reject the redundant form.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: 100));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("equals --root-id");
    }

    [Fact]
    public async Task EnsurePlan_ParentItemNegative_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: -7));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("--parent-item-id must be positive");
    }

    // ─── Root plan branch ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsurePlan_RootPlanAlreadyExistsLocally_ChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLocalBranchExists(runner, "plan/100", exists: true);
        StubCheckout(runner, "plan/100");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Branch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
        result.IsRootPlan.ShouldBeTrue();
        result.ParentItemId.ShouldBeNull();
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(100);
    }

    [Fact]
    public async Task EnsurePlan_RootPlanMissing_CreatesFromFeature()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: false);
        StubLocalBranchExists(runner, "plan/100", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true);
        StubCreateBranch(runner, "plan/100", "feature/100");
        StubPush(runner, "plan/100");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("created");
        result.Pushed.ShouldBeTrue();
        result.BaseRemoteExisted.ShouldBeTrue();
        result.BaseFetched.ShouldBeFalse();
        result.CreatedFrom.ShouldBe("feature/100");
        result.IsRootPlan.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsurePlan_RootPlanFeatureMissing_PointsAtEnsureFeature()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: false);
        StubLocalBranchExists(runner, "plan/100", exists: false);
        StubLsRemote(runner, "feature/100", exists: false); // base missing

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("does not exist on remote");
        result.Error!.ShouldContain("ensure-feature");
        result.Action.ShouldBe("error");
    }

    [Fact]
    public async Task EnsurePlan_RootPlanRemoteOnly_FetchesAndChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLocalBranchExists(runner, "plan/100", exists: false);
        StubFetch(runner, "plan/100");
        StubCheckoutTracking(runner, "plan/100");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsurePlan_RootPlanLocalOnly_PushesToRemote()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: false);
        StubLocalBranchExists(runner, "plan/100", exists: true);
        StubCheckout(runner, "plan/100");
        StubPush(runner, "plan/100");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeTrue();
        result.RemoteExisted.ShouldBeFalse();
    }

    // ─── Child of root plan (no --parent-item-id) ────────────────────────

    [Fact]
    public async Task EnsurePlan_ChildOfRootMissing_CreatesFromRootPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: false);
        StubLocalBranchExists(runner, "plan/100-50", exists: false);
        StubLsRemote(runner, "plan/100", exists: true);
        StubLocalBranchExists(runner, "plan/100", exists: true);
        StubCreateBranch(runner, "plan/100-50", "plan/100");
        StubPush(runner, "plan/100-50");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Branch.ShouldBe("plan/100-50");
        result.BaseBranch.ShouldBe("plan/100");
        result.Action.ShouldBe("created");
        result.IsRootPlan.ShouldBeFalse();
        result.ParentItemId.ShouldBeNull(); // implicit parent = root plan
        result.CreatedFrom.ShouldBe("plan/100");
    }

    [Fact]
    public async Task EnsurePlan_ChildOfRootRootPlanMissing_PointsAtEnsureRootPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: false);
        StubLocalBranchExists(runner, "plan/100-50", exists: false);
        StubLsRemote(runner, "plan/100", exists: false); // root plan missing

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("does not exist on remote");
        result.Error!.ShouldContain("ensure-plan");
        result.Error!.ShouldContain("root plan first");
    }

    [Fact]
    public async Task EnsurePlan_ChildOfRootRootPlanRemoteOnly_FetchesThenCreates()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: false);
        StubLocalBranchExists(runner, "plan/100-50", exists: false);
        StubLsRemote(runner, "plan/100", exists: true);
        StubLocalBranchExists(runner, "plan/100", exists: false);
        StubFetch(runner, "plan/100");
        StubCheckoutTracking(runner, "plan/100");
        StubCreateBranch(runner, "plan/100-50", "plan/100");
        StubPush(runner, "plan/100-50");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("created");
        result.BaseFetched.ShouldBeTrue();
    }

    // ─── Descendant plan (--parent-item-id provided) ─────────────────────

    [Fact]
    public async Task EnsurePlan_DescendantMissing_CreatesFromParentPlan()
    {
        // plan/100-50 → plan/100-25 (item 50's parent is item 25, not the root)
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: false);
        StubLocalBranchExists(runner, "plan/100-50", exists: false);
        StubLsRemote(runner, "plan/100-25", exists: true);
        StubLocalBranchExists(runner, "plan/100-25", exists: true);
        StubCreateBranch(runner, "plan/100-50", "plan/100-25");
        StubPush(runner, "plan/100-50");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: 25));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Branch.ShouldBe("plan/100-50");
        result.BaseBranch.ShouldBe("plan/100-25");
        result.Action.ShouldBe("created");
        result.IsRootPlan.ShouldBeFalse();
        result.ParentItemId.ShouldBe(25);
        result.CreatedFrom.ShouldBe("plan/100-25");
    }

    [Fact]
    public async Task EnsurePlan_DescendantParentMissing_PointsAtEnsurePlan()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: false);
        StubLocalBranchExists(runner, "plan/100-50", exists: false);
        StubLsRemote(runner, "plan/100-25", exists: false); // parent missing

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: 25));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Error!.ShouldContain("does not exist on remote");
        result.Error!.ShouldContain("ensure-plan");
        result.Error!.ShouldContain("--item-id 25"); // remediation hint references parent id
    }

    [Fact]
    public async Task EnsurePlan_DescendantAlreadyExistsLocally_ChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-50", exists: true);
        StubLocalBranchExists(runner, "plan/100-50", exists: true);
        StubCheckout(runner, "plan/100-50");
        StubLsRemote(runner, "plan/100-25", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 50, parentItemId: 25));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsurePlanResult)!;
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.ParentItemId.ShouldBe(25);
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task EnsurePlan_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLocalBranchExists(runner, "plan/100", exists: true);
        StubCheckout(runner, "plan/100");
        StubLsRemote(runner, "feature/100", exists: true);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.EnsurePlan(rootId: 100, itemId: 100));

        // Lock the JSON wire contract that workflow YAMLs read.
        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"remote_existed\"");
        output.ShouldContain("\"pushed\"");
        output.ShouldContain("\"base_remote_existed\"");
        output.ShouldContain("\"base_fetched\"");
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"is_root_plan\"");
    }
}
