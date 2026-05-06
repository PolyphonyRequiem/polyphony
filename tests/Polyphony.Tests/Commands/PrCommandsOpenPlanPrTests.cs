using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr open-plan-pr</c>. Stubs all
/// shell-outs (git ls-remote, gh pr list, gh pr view, gh pr create,
/// twig show) via <see cref="FakeProcessRunner"/> and asserts on the
/// platform-neutral JSON the verb emits.
///
/// <para>Each test passes an explicit <c>--manifest-path</c> pointing
/// at a freshly-written file in <see cref="Path.GetTempPath"/>, so the
/// fixture has no cwd state and no <see cref="IDisposable"/> needs.</para>
/// </summary>
public sealed class PrCommandsOpenPlanPrTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git)), runner);
    }

    private static string SeedManifest(int rootId, Dictionary<string, int>? planGenerations = null)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "polyphony-tests-" + Guid.NewGuid().ToString("N") + ".yaml");
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal),
        };
        RunManifestStore.Save(path, manifest);
        return path;
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubGitOriginUrl(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubTwigShow(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--tree", "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url, string headRef)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"{{headRef}}"}]""", ""));

    private static void StubPrView(FakeProcessRunner runner, int prNumber, string body)
    {
        var encodedBody = JsonSerializer.Serialize(body);
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "OPEN",
              "reviewDecision": "REVIEW_REQUIRED",
              "mergeable": "MERGEABLE",
              "headRefName": "plan/x",
              "headRefOid": "abc123",
              "baseRefName": "plan/y",
              "mergedAt": null,
              "mergeCommit": null,
              "body": {{encodedBody}},
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, json, ""));
    }

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    private static PrOpenPlanPrResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenPlanPrResult)!;

    // ─── Input validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(-1, 100, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(100, -5, 0)]
    public async Task OpenPlanPr_InvalidIds_ReturnsConfigError(int rootId, int itemId, int parentItemId)
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: rootId, itemId: itemId, parentItemId: parentItemId,
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task OpenPlanPr_RootPlanWithParentItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, parentItemId: 50,
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("--parent-item-id must not be provided");
    }

    [Fact]
    public async Task OpenPlanPr_RootPlanWithAncestors_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, ancestorIds: "5678,root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("root plan must not declare ancestors");
    }

    [Fact]
    public async Task OpenPlanPr_DescendantWithoutAncestors_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("--ancestor-ids must list");
    }

    [Fact]
    public async Task OpenPlanPr_AncestorChainContainsItem_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "5678,root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("must not appear in its own ancestor chain");
    }

    [Fact]
    public async Task OpenPlanPr_DuplicateAncestor_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 9999, ancestorIds: "5678,5678,root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("duplicate entry");
    }

    [Fact]
    public async Task OpenPlanPr_MalformedAncestor_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 9999, ancestorIds: "abc,root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("must be 'root' or a positive numeric");
    }

    [Fact]
    public async Task OpenPlanPr_ParentEqualsItem_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, parentItemId: 5678, ancestorIds: "root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("must not equal --item-id");
    }

    [Fact]
    public async Task OpenPlanPr_ParentEqualsRoot_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, parentItemId: 100, ancestorIds: "root",
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("omit --parent-item-id");
    }

    // ─── Manifest read errors ────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_ManifestMissing_ReturnsCacheError()
    {
        var (cmd, _) = CreateCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(),
            "polyphony-missing-" + Guid.NewGuid().ToString("N") + ".yaml");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: bogusPath));
        exit.ShouldBe(ExitCodes.CacheError);
        Parse(output).Error!.ShouldContain("manifest not found");
    }

    // ─── Branch existence ────────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_HeadBranchMissingOnRemote_ReturnsRoutingFailure()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = Parse(output);
        result.Error!.ShouldContain("head branch 'plan/100' does not exist");
        result.HeadBranch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
    }

    [Fact]
    public async Task OpenPlanPr_BaseBranchMissingOnRemote_ReturnsRoutingFailure()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLsRemote(runner, "feature/100", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Error!.ShouldContain("base branch 'feature/100' does not exist");
    }

    // ─── Happy paths: create new PR ──────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_RootPlan_CreatesNewPr()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLsRemote(runner, "feature/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 100, "Authentication overhaul");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/42");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.Stale.ShouldBeFalse();
        result.IsRootPlan.ShouldBeTrue();
        result.HeadBranch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://github.com/owner/repo/pull/42");
        result.RepoSlug.ShouldBe("owner/repo");
        result.ItemKey.ShouldBe("root");
        result.RequestsParentChange.ShouldBeFalse();
        result.AncestorPlanGenerations.ShouldBeEmpty();
        result.Title.ShouldContain("Authentication overhaul");
    }

    [Fact]
    public async Task OpenPlanPr_ChildOfRoot_CreatesNewPr()
    {
        var manifestPath = SeedManifest(rootId: 100, planGenerations: new() { ["root"] = 2 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubLsRemote(runner, "plan/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 5678, "Login screen");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/43");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "root",
                manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.IsRootPlan.ShouldBeFalse();
        result.HeadBranch.ShouldBe("plan/100-5678");
        result.BaseBranch.ShouldBe("plan/100");
        result.ItemKey.ShouldBe("5678");
        result.AncestorPlanGenerations.Count.ShouldBe(1);
        result.AncestorPlanGenerations["root"].ShouldBe(2);
    }

    [Fact]
    public async Task OpenPlanPr_DescendantWithExplicitParent_CreatesNewPr()
    {
        var manifestPath = SeedManifest(rootId: 100,
            planGenerations: new() { ["root"] = 1, ["5678"] = 3 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-9999", exists: true);
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 9999, "Detail page");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/44");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(
                rootId: 100,
                itemId: 9999,
                parentItemId: 5678,
                ancestorIds: "5678,root",
                manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.HeadBranch.ShouldBe("plan/100-9999");
        result.BaseBranch.ShouldBe("plan/100-5678");
        result.ParentItemId.ShouldBe(5678);
        result.AncestorPlanGenerations.Count.ShouldBe(2);
        result.AncestorPlanGenerations["5678"].ShouldBe(3);
        result.AncestorPlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public async Task OpenPlanPr_AncestorMissingFromManifest_DefaultsToZero()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubLsRemote(runner, "plan/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 5678, null);
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/45");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "root",
                manifestPath: manifestPath));
        Parse(output).AncestorPlanGenerations["root"].ShouldBe(0);
    }

    [Fact]
    public async Task OpenPlanPr_PrCreateBodyContainsFrontMatter()
    {
        var manifestPath = SeedManifest(rootId: 100,
            planGenerations: new() { ["root"] = 2, ["5678"] = 4 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-9999", exists: true);
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 9999, "Detail");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/50");

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(
                rootId: 100, itemId: 9999, parentItemId: 5678,
                ancestorIds: "5678,root", manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);

        var createInvocation = runner.Invocations.LastOrDefault(i =>
            i.Executable == "gh"
            && i.Arguments.Count >= 2
            && i.Arguments[0] == "pr"
            && i.Arguments[1] == "create");
        createInvocation.ShouldNotBeNull();
        var bodyIndex = createInvocation.Arguments.ToList().IndexOf("--body");
        bodyIndex.ShouldBeGreaterThan(-1);
        var body = createInvocation.Arguments[bodyIndex + 1];
        body.ShouldStartWith("---\n");
        body.ShouldContain("requests_parent_change: false");
        body.ShouldContain("ancestor_plan_generations:");
        body.ShouldContain("\"5678\": 4");
        body.ShouldContain("root: 2");

        // Round-trip: feed the emitted body back through PlanPrFrontMatter.
        var roundTripped = PlanPrFrontMatter.Parse(body);
        roundTripped.RequestsParentChange.ShouldBeFalse();
        roundTripped.AncestorPlanGenerations.Count.ShouldBe(2);
        roundTripped.AncestorPlanGenerations["5678"].ShouldBe(4);
        roundTripped.AncestorPlanGenerations["root"].ShouldBe(2);
    }

    // ─── Reuse semantics ─────────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_ExistingPrWithMatchingSnapshot_ReturnsReuse()
    {
        var manifestPath = SeedManifest(rootId: 100, planGenerations: new() { ["root"] = 2 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubLsRemote(runner, "plan/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 5678, "Login");
        StubPrListExisting(runner, 42, "https://github.com/owner/repo/pull/42", "plan/100-5678");
        StubPrView(runner, 42,
            "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 2\n---\n## Plan");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "root",
                manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Created.ShouldBeFalse();
        result.Stale.ShouldBeFalse();
        result.PrNumber.ShouldBe(42);
        result.AncestorPlanGenerations["root"].ShouldBe(2);
    }

    [Fact]
    public async Task OpenPlanPr_ExistingPrWithStaleSnapshot_ReturnsStale()
    {
        var manifestPath = SeedManifest(rootId: 100, planGenerations: new() { ["root"] = 5 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubLsRemote(runner, "plan/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 5678, "Login");
        StubPrListExisting(runner, 42, "https://github.com/owner/repo/pull/42", "plan/100-5678");
        StubPrView(runner, 42,
            "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 2\n---\n## Plan");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "root",
                manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = Parse(output);
        result.Created.ShouldBeFalse();
        result.Stale.ShouldBeTrue();
        result.PrNumber.ShouldBe(42);
        result.Error!.ShouldContain("stale");
        result.Error!.ShouldContain("root:2");
        result.Error!.ShouldContain("root:5");
    }

    [Fact]
    public async Task OpenPlanPr_ExistingPrWithNoFrontMatter_TreatedAsStaleWhenSnapshotNonEmpty()
    {
        var manifestPath = SeedManifest(rootId: 100, planGenerations: new() { ["root"] = 1 });
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100-5678", exists: true);
        StubLsRemote(runner, "plan/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 5678, "Login");
        StubPrListExisting(runner, 42, "https://github.com/owner/repo/pull/42", "plan/100-5678");
        StubPrView(runner, 42, "## Just a plan body, no front-matter");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 5678, ancestorIds: "root",
                manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Stale.ShouldBeTrue();
    }

    // ─── PR creation failure ─────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_GhCreateReturnsEmptyUrl_ReturnsRoutingFailure()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLsRemote(runner, "feature/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 100, "Root");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Error!.ShouldContain("gh pr create");
    }

    // ─── JSON wire contract ──────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanPr_JsonContract_IsSnakeCase()
    {
        var manifestPath = SeedManifest(rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: true);
        StubLsRemote(runner, "feature/100", exists: true);
        StubGitOriginUrl(runner, "https://github.com/owner/repo.git");
        StubTwigShow(runner, 100, "Root");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/owner/repo/pull/1");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanPr(rootId: 100, itemId: 100, manifestPath: manifestPath));
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"item_key\"");
        output.ShouldContain("\"is_root_plan\"");
        output.ShouldContain("\"head_branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"created\"");
        output.ShouldContain("\"stale\"");
        output.ShouldContain("\"requests_parent_change\"");
        output.ShouldContain("\"ancestor_plan_generations\"");
    }
}
