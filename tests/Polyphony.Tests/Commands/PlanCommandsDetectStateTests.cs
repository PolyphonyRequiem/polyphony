using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony plan detect-state</c>. Each test composes the
/// minimal stub set (slug → ls-remote → gh pr list → gh pr view → twig show)
/// needed for a single state-machine arm and asserts the emitted JSON.
/// </summary>
public sealed class PlanCommandsDetectStateTests : CommandTestBase
{
    private const int RootId = 100;
    private const int ChildId = 200;
    private const string RootPlanBranch = "plan/100";
    private const string ChildPlanBranch = "plan/100-200";
    private const string FeatureBranch = "feature/100";
    private const string ManifestPath = ".polyphony/run.yaml";

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new FakePostconditionVerifier()), runner);
    }

    private static PlanDetectStateResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDetectStateResult)!;

    // ─── Stubs ────────────────────────────────────────────────────────────

    private static void StubRemoteUrl(FakeProcessRunner runner, string url = "https://github.com/acme/repo.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListSingle(FakeProcessRunner runner, int number, string headRef, string url = "https://gh/pr/1")
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0,
                $$"""[{"number":{{number}},"headRefName":"{{headRef}}","url":"{{url}}"}]""",
                ""));

    private static void StubPrListMulti(FakeProcessRunner runner, params (int Number, string HeadRef, string Url)[] prs)
    {
        var json = "[" + string.Join(",", prs.Select(p =>
            $$"""{"number":{{p.Number}},"headRefName":"{{p.HeadRef}}","url":"{{p.Url}}"}""")) + "]";
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, json, ""));
    }

    private static void StubPrPoll(FakeProcessRunner runner, int prNumber, string state, string body = "")
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "REVIEW_REQUIRED",
              "mergeable": "MERGEABLE",
              "headRefName": "plan/100",
              "headRefOid": "abc123",
              "baseRefName": "feature/100",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubTwigShowWithTags(FakeProcessRunner runner, int itemId, string tags)
        => runner.WhenExact("twig", ["show", itemId.ToString(), "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{itemId}},"title":"Item","tags":"{{tags}}"}""", ""));

    private static void StubGitShowManifest(FakeProcessRunner runner, string yaml)
        => runner.WhenExact("git", ["show", $"origin/{FeatureBranch}:{ManifestPath}"],
            new ProcessResult(0, yaml, ""));

    private static string MakeManifest(IDictionary<string, int> planGenerations)
    {
        var manifest = new Polyphony.Manifest.RunManifest
        {
            Schema = 1,
            RootId = RootId,
            PlatformProject = "github.com/acme/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = new Dictionary<string, int>(planGenerations, StringComparer.Ordinal),
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.yaml");
        try
        {
            Polyphony.Manifest.RunManifestStore.Save(tmp, manifest);
            return File.ReadAllText(tmp);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string MakePrBodyWithSnapshot(IDictionary<string, int> snapshot)
    {
        var lines = new List<string>
        {
            "---",
            "requests_parent_change: false",
            "ancestor_plan_generations:",
        };
        foreach (var (k, v) in snapshot)
        {
            lines.Add($"  \"{k}\": {v}");
        }
        lines.Add("---");
        lines.Add("");
        lines.Add("Plan PR body");
        return string.Join("\n", lines);
    }

    // ─── Argument validation ──────────────────────────────────────────────

    [Fact]
    public async Task DetectState_NegativeRootId_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: -1, itemId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task DetectState_ZeroItemId_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: 100, itemId: 0));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task DetectState_NoOriginRemote_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: No such remote 'origin'"));
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("slug");
    }

    // ─── not_started ──────────────────────────────────────────────────────

    [Fact]
    public async Task DetectState_NoBranchNoPr_NotStarted()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.PlanBranch.ShouldBe(ChildPlanBranch);
        result.BranchExistsOnOrigin.ShouldBeFalse();
        result.PrNumber.ShouldBeNull();
    }

    [Fact]
    public async Task DetectState_BranchExistsButNoPr_NotStarted()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.BranchExistsOnOrigin.ShouldBeTrue();
        result.PrNumber.ShouldBeNull();
    }

    // ─── awaiting_review ──────────────────────────────────────────────────

    [Fact]
    public async Task DetectState_OpenPr_AwaitingReview()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch, "https://gh/pr/42");
        StubPrPoll(runner, 42, "OPEN");
        // No manifest stub needed — empty front-matter means no stale check fires.

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://gh/pr/42");
        result.PrState.ShouldBe("OPEN");
    }

    [Fact]
    public async Task DetectState_OpenPr_PicksHighestNumber()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListMulti(runner,
            (40, ChildPlanBranch, "https://gh/pr/40"),
            (99, ChildPlanBranch, "https://gh/pr/99"),
            (50, ChildPlanBranch, "https://gh/pr/50"));
        StubPrPoll(runner, 99, "OPEN");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
        result.PrNumber.ShouldBe(99);
    }

    // ─── stale_generation ─────────────────────────────────────────────────

    [Fact]
    public async Task DetectState_OpenPrWithStaleSnapshot_StaleGeneration()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        // PR snapshot says root was at gen 1; manifest now says root is at gen 2 → stale.
        StubPrPoll(runner, 42, "OPEN", MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 2 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("stale_generation");
        result.PrNumber.ShouldBe(42);
        result.StaleAncestors.ShouldHaveSingleItem();
        result.StaleAncestors[0].ShouldContain("root");
        result.StaleAncestors[0].ShouldContain("snapshot=1");
        result.StaleAncestors[0].ShouldContain("manifest=2");
    }

    [Fact]
    public async Task DetectState_OpenPrWithCurrentSnapshot_AwaitingReview()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        // Equal generations — not stale.
        StubPrPoll(runner, 42, "OPEN", MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 }));
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 2 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
        result.StaleAncestors.ShouldBeEmpty();
    }

    [Fact]
    public async Task DetectState_OpenPrManifestMissing_AwaitingReview()
    {
        // Missing manifest at the expected ref means we have no signal to
        // claim staleness — fall through to awaiting_review.
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "OPEN", MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));
        runner.WhenExact("git", ["show", $"origin/{FeatureBranch}:{ManifestPath}"],
            new ProcessResult(128, "", $"fatal: path '{ManifestPath}' does not exist"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
    }

    // ─── closed_unmerged ──────────────────────────────────────────────────

    [Fact]
    public async Task DetectState_ClosedPr_ClosedUnmerged()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch, "https://gh/pr/42");
        StubPrPoll(runner, 42, "CLOSED");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("closed_unmerged");
        result.PrNumber.ShouldBe(42);
        result.PrState.ShouldBe("CLOSED");
    }

    // ─── merged_unseeded vs complete ──────────────────────────────────────

    [Fact]
    public async Task DetectState_MergedPrNoPlannedTag_MergedUnseeded()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;something:else");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("merged_unseeded");
        result.PrState.ShouldBe("MERGED");
    }

    [Fact]
    public async Task DetectState_MergedPrWithPlannedTag_Complete()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("complete");
    }

    [Fact]
    public async Task DetectState_MergedPrTwigShowMissing_MergedUnseeded()
    {
        // twig show failure is treated as "not seeded" — the plan workflow
        // will run plan seed-children to recover.
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");
        runner.WhenExact("twig", ["show", ChildId.ToString(), "--output", "json"],
            new ProcessResult(1, "", "not found"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("merged_unseeded");
    }

    // ─── Root vs descendant branch naming ────────────────────────────────

    [Fact]
    public async Task DetectState_RootPlan_UsesRootBranchName()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, RootPlanBranch, exists: false);
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: RootId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.PlanBranch.ShouldBe(RootPlanBranch);
        result.State.ShouldBe("not_started");
    }

    // ─── Custom planned-tag override ──────────────────────────────────────

    // ─── parent_change_pending ────────────────────────────────────────────

    private static void StubPrPollWithReview(
        FakeProcessRunner runner,
        int prNumber,
        string state,
        string reviewDecision,
        string headRef,
        string body = "")
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "{{reviewDecision}}",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRef}}",
              "headRefOid": "abc123",
              "baseRefName": "feature/100",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubTwigShowTreeWithChildren(
        FakeProcessRunner runner,
        int parentId,
        params int[] childIds)
    {
        var children = string.Join(",", childIds.Select(id => $$"""{"id":{{id}},"title":"Child {{id}}"}"""));
        var json = $$"""{"id":{{parentId}},"title":"Parent","children":[{{children}}]}""";
        runner.WhenExact("twig", ["show", parentId.ToString(), "--tree", "--output", "json"],
            new ProcessResult(0, json, ""));
    }

    /// <summary>
    /// Build a plan-PR body whose front-matter has both
    /// <c>requests_parent_change</c> set explicitly AND a snapshot map.
    /// </summary>
    private static string MakePrBodyWithFlag(bool requestsParentChange, IDictionary<string, int> snapshot)
    {
        var lines = new List<string>
        {
            "---",
            $"requests_parent_change: {(requestsParentChange ? "true" : "false")}",
            "ancestor_plan_generations:",
        };
        foreach (var (k, v) in snapshot)
        {
            lines.Add($"  \"{k}\": {v}");
        }
        lines.Add("---");
        lines.Add("");
        lines.Add("Plan PR body");
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task DetectState_CompleteWithNoChildren_StaysComplete()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 }));
        StubTwigShowTreeWithChildren(runner, ChildId);  // no children

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("complete");
        result.ParentChangePendingChildren.ShouldBeEmpty();
    }

    [Fact]
    public async Task DetectState_CompleteWithChildButPrPending_StaysComplete()
    {
        var (cmd, runner) = CreateCommand();
        const int grandchildId = 300;
        const string grandchildPlanBranch = "plan/100-300";

        // Parent setup → reaches "complete".
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", ChildPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":42,"headRefName":"{{ChildPlanBranch}}","url":"https://gh/pr/42"}]""", ""));
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        // Child of itemId=200 has a PR but it's pending review (not approved).
        StubTwigShowTreeWithChildren(runner, ChildId, grandchildId);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", grandchildPlanBranch,
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":99,"headRefName":"{{grandchildPlanBranch}}","url":"https://gh/pr/99"}]""", ""));
        StubPrPollWithReview(runner, 99, "OPEN", "REVIEW_REQUIRED", grandchildPlanBranch,
            MakePrBodyWithFlag(true, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("complete");
        result.ParentChangePendingChildren.ShouldBeEmpty();
    }

    [Fact]
    public async Task DetectState_CompleteWithApprovedChildButFlagFalse_StaysComplete()
    {
        var (cmd, runner) = CreateCommand();
        const int grandchildId = 300;
        const string grandchildPlanBranch = "plan/100-300";

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", ChildPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":42,"headRefName":"{{ChildPlanBranch}}","url":"https://gh/pr/42"}]""", ""));
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        StubTwigShowTreeWithChildren(runner, ChildId, grandchildId);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", grandchildPlanBranch,
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":99,"headRefName":"{{grandchildPlanBranch}}","url":"https://gh/pr/99"}]""", ""));
        // Approved but flag is false.
        StubPrPollWithReview(runner, 99, "OPEN", "APPROVED", grandchildPlanBranch,
            MakePrBodyWithFlag(false, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("complete");
    }

    [Fact]
    public async Task DetectState_CompleteWithApprovedChildButStaleSnapshot_StaysComplete()
    {
        var (cmd, runner) = CreateCommand();
        const int grandchildId = 300;
        const string grandchildPlanBranch = "plan/100-300";

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", ChildPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":42,"headRefName":"{{ChildPlanBranch}}","url":"https://gh/pr/42"}]""", ""));
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        // Manifest says parent gen=2; child snapshot says gen=1 → stale → not pending.
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 2 }));

        StubTwigShowTreeWithChildren(runner, ChildId, grandchildId);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", grandchildPlanBranch,
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":99,"headRefName":"{{grandchildPlanBranch}}","url":"https://gh/pr/99"}]""", ""));
        StubPrPollWithReview(runner, 99, "OPEN", "APPROVED", grandchildPlanBranch,
            MakePrBodyWithFlag(true, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("complete");
    }

    [Fact]
    public async Task DetectState_CompleteWithApprovedChildRequestingChange_ParentChangePending()
    {
        var (cmd, runner) = CreateCommand();
        const int grandchildId = 300;
        const string grandchildPlanBranch = "plan/100-300";

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", ChildPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":42,"headRefName":"{{ChildPlanBranch}}","url":"https://gh/pr/42"}]""", ""));
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        StubTwigShowTreeWithChildren(runner, ChildId, grandchildId);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", grandchildPlanBranch,
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":99,"headRefName":"{{grandchildPlanBranch}}","url":"https://gh/pr/99"}]""", ""));
        StubPrPollWithReview(runner, 99, "OPEN", "APPROVED", grandchildPlanBranch,
            MakePrBodyWithFlag(true, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("parent_change_pending");
        result.ParentChangePendingChildren.ShouldHaveSingleItem();
        var entry = result.ParentChangePendingChildren[0];
        entry.ChildItemId.ShouldBe(grandchildId);
        entry.PrNumber.ShouldBe(99);
        entry.ExpectedParentGeneration.ShouldBe(1);
    }

    [Fact]
    public async Task DetectState_RootCompleteWithApprovedChildRequestingChange_UsesRootKey()
    {
        // Parent IS root → snapshot key must be "root", not the numeric id.
        var (cmd, runner) = CreateCommand();
        const int childOfRoot = 200;
        const string childOfRootPlanBranch = "plan/100-200";

        StubRemoteUrl(runner);
        StubLsRemote(runner, RootPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", RootPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":11,"headRefName":"{{RootPlanBranch}}","url":"https://gh/pr/11"}]""", ""));
        StubPrPoll(runner, 11, "MERGED");
        StubTwigShowWithTags(runner, RootId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 1 }));

        StubTwigShowTreeWithChildren(runner, RootId, childOfRoot);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", childOfRootPlanBranch,
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":77,"headRefName":"{{childOfRootPlanBranch}}","url":"https://gh/pr/77"}]""", ""));
        StubPrPollWithReview(runner, 77, "OPEN", "APPROVED", childOfRootPlanBranch,
            MakePrBodyWithFlag(true, new Dictionary<string, int> { ["root"] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: RootId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("parent_change_pending");
        result.ParentChangePendingChildren.ShouldHaveSingleItem();
        result.ParentChangePendingChildren[0].ChildItemId.ShouldBe(childOfRoot);
    }

    [Fact]
    public async Task DetectState_CompleteWithMultipleQualifyingChildren_AllListed()
    {
        var (cmd, runner) = CreateCommand();
        const int grandchildA = 300;
        const int grandchildB = 400;

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", ChildPlanBranch,
                "--state", "all", "--limit", "50",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":42,"headRefName":"{{ChildPlanBranch}}","url":"https://gh/pr/42"}]""", ""));
        StubPrPoll(runner, 42, "MERGED");
        StubTwigShowWithTags(runner, ChildId, tags: "polyphony;polyphony:planned");
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        StubTwigShowTreeWithChildren(runner, ChildId, grandchildA, grandchildB);

        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", $"plan/100-{grandchildA}",
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":71,"headRefName":"plan/100-{{grandchildA}}","url":"https://gh/pr/71"}]""", ""));
        StubPrPollWithReview(runner, 71, "OPEN", "APPROVED", $"plan/100-{grandchildA}",
            MakePrBodyWithFlag(true, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        runner.WhenExact("gh",
            ["pr", "list", "--repo", "acme/repo", "--head", $"plan/100-{grandchildB}",
                "--state", "open", "--limit", "10",
                "--json", "number,headRefName,url,mergedAt"],
            new ProcessResult(0, $$"""[{"number":72,"headRefName":"plan/100-{{grandchildB}}","url":"https://gh/pr/72"}]""", ""));
        StubPrPollWithReview(runner, 72, "OPEN", "APPROVED", $"plan/100-{grandchildB}",
            MakePrBodyWithFlag(true, new Dictionary<string, int> { [ChildId.ToString()] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(RootId, ChildId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("parent_change_pending");
        result.ParentChangePendingChildren.Count.ShouldBe(2);
        result.ParentChangePendingChildren.Select(c => c.ChildItemId)
            .ShouldBe(new[] { grandchildA, grandchildB }, ignoreOrder: true);
    }
}
