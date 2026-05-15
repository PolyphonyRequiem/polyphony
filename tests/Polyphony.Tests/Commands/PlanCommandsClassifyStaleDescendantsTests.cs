using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony plan classify-stale-descendants</c>. Each test
/// seeds a small work-item tree in the in-memory cache, stubs gh/git/twig
/// via <see cref="FakeProcessRunner"/>, and asserts on the JSON the
/// verb emits.
/// </summary>
public sealed class PlanCommandsClassifyStaleDescendantsTests : CommandTestBase, IDisposable
{
    private const int RootId = 100;
    private const int ChildA = 200;
    private const int ChildB = 300;
    private const string FeatureBranch = "feature/100";

    private readonly string tempCommonDir;
    private readonly string localManifestPath;

    public PlanCommandsClassifyStaleDescendantsTests()
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

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        // Stub git common-dir so PolyphonyStatePaths resolves into our temp dir.
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, this.tempCommonDir + "\n", ""));
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var git = new GitClient(runner);
        return (new PlanCommands(walker, Repository, Config, twig, git, new GhClient(runner), new ThrowingAdoClient(), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git), new Polyphony.Sdlc.Observers.RepoIdentityResolver(git), new Polyphony.Sdlc.Observers.PullRequestReader(new GhClient(runner), null)), runner);
    }

    private static PlanClassifyStaleDescendantsResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanClassifyStaleDescendantsResult)!;

    // ─── Stubs ────────────────────────────────────────────────────────────

    private static void StubRemoteUrl(FakeProcessRunner runner, string url = "https://github.com/acme/repo.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    // Rev 4.2: manifest is read from local disk under <git-common-dir>/polyphony/{rootId}/run.yaml.
    // Helpers below seed that file rather than stubbing `git show origin/...:.polyphony/run.yaml`.
    private void StubGitShowManifest(FakeProcessRunner runner, string yaml)
    {
        _ = runner; // signature kept for call-site compat
        File.WriteAllText(this.localManifestPath, yaml);
    }

    private void StubGitShowManifestMissing(FakeProcessRunner runner)
    {
        _ = runner;
        if (File.Exists(this.localManifestPath)) File.Delete(this.localManifestPath);
    }

    private void StubGitShowManifestFatal(FakeProcessRunner runner)
    {
        _ = runner;
        File.WriteAllText(this.localManifestPath, "::: not yaml :::");
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListForBranch(
        FakeProcessRunner runner, string branch, params (int Number, string HeadRef, string Url)[] prs)
    {
        var json = "[" + string.Join(",", prs.Select(p =>
            $$"""{"number":{{p.Number}},"headRefName":"{{p.HeadRef}}","url":"{{p.Url}}"}""")) + "]";
        runner.WhenStartsWith("gh", ["pr", "list", "--repo", "acme/repo", "--head", branch],
            new ProcessResult(0, json, ""));
    }

    private static void StubPrPoll(FakeProcessRunner runner, int prNumber, string headRef, string body)
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "OPEN",
              "reviewDecision": "REVIEW_REQUIRED",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRef}}",
              "headRefOid": "abc{{prNumber}}",
              "baseRefName": "{{FeatureBranch}}",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

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
    public async Task ClassifyStaleDescendants_ZeroRootId_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(rootId: 0));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_root_id");
        result.Error!.ShouldContain("positive");
    }

    [Fact]
    public async Task ClassifyStaleDescendants_NoOriginRemote_EmitsNoSlugError()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: No such remote 'origin'"));
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("no_slug");
    }

    // ─── Manifest read failures ───────────────────────────────────────────

    [Fact]
    public async Task ClassifyStaleDescendants_ManifestMissing_EmitsManifestNotFound()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifestMissing(runner);
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_not_found");
        // Rev 4.2: manifest is local now; no remote ref.
        result.ManifestPath.ShouldBe(this.localManifestPath);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_ManifestReadFatal_EmitsManifestReadFailed()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifestFatal(runner);
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        // Rev 4.2: parse failures collapse into manifest_invalid.
        result.ErrorCode.ShouldBe("manifest_invalid");
    }

    [Fact]
    public async Task ClassifyStaleDescendants_ManifestInvalidYaml_EmitsManifestInvalid()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, "::not yaml::\n  - [\n");
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_invalid");
    }

    [Fact]
    public async Task ClassifyStaleDescendants_ManifestRootIdMismatch_EmitsRootIdMismatch()
    {
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        // Manifest claims root=999 but we pass rootId=100.
        var manifest = new Polyphony.Manifest.RunManifest
        {
            Schema = 1,
            RootId = 999,
            PlatformProject = "github.com/acme/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.yaml");
        Polyphony.Manifest.RunManifestStore.Save(tmp, manifest);
        var yaml = File.ReadAllText(tmp);
        File.Delete(tmp);
        StubGitShowManifest(runner, yaml);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("root_id_mismatch");
    }

    // ─── Empty trees / no PRs ─────────────────────────────────────────────

    [Fact]
    public async Task ClassifyStaleDescendants_NoDescendants_EmitsEmptyResult()
    {
        await SeedAsync(new WorkItemBuilder().WithId(RootId).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 1 }));
        // No descendants → no gh pr list calls fire.

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.TotalDescendantsScanned.ShouldBe(0);
        result.TotalDescendantsWithOpenPrs.ShouldBe(0);
        result.TotalStale.ShouldBe(0);
        result.StaleDescendants.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClassifyStaleDescendants_DescendantsWithoutPrs_EmitsEmptyStale()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 2 }));
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.TotalDescendantsScanned.ShouldBe(1);
        result.TotalDescendantsWithOpenPrs.ShouldBe(0);
        result.TotalStale.ShouldBe(0);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_PrSnapshotCurrent_NotStale()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        // Manifest root @ 2; PR snapshot also root @ 2 → not stale.
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 2 }));
        StubPrListForBranch(runner, "plan/100-200", (501, "plan/100-200", "https://gh/pr/501"));
        StubPrPoll(runner, 501, "plan/100-200",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalDescendantsScanned.ShouldBe(1);
        result.TotalDescendantsWithOpenPrs.ShouldBe(1);
        result.TotalStale.ShouldBe(0);
    }

    // ─── Stale classification ─────────────────────────────────────────────

    [Fact]
    public async Task ClassifyStaleDescendants_RootGenerationAdvanced_FlagsDescendantStale()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        // Manifest root @ 3; PR snapshot root @ 1 → stale by 2.
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 3 }));
        StubPrListForBranch(runner, "plan/100-200", (501, "plan/100-200", "https://gh/pr/501"));
        StubPrPoll(runner, 501, "plan/100-200",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalStale.ShouldBe(1);
        var stale = result.StaleDescendants[0];
        stale.ItemId.ShouldBe(ChildA);
        stale.ParentItemId.ShouldBe(RootId);
        stale.PrNumber.ShouldBe(501);
        stale.HeadRef.ShouldBe("plan/100-200");
        stale.HeadSha.ShouldBe("abc501");
        stale.StaleAncestors.Count.ShouldBe(1);
        stale.StaleAncestors[0].AncestorKey.ShouldBe("root");
        stale.StaleAncestors[0].SnapshotGeneration.ShouldBe(1);
        stale.StaleAncestors[0].CurrentGeneration.ShouldBe(3);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_MultipleStaleAncestors_AllReported()
    {
        // Tree: root → 200 → 250 (grandchild). Grandchild's PR snapshot
        // is behind for both root and parent (200).
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build(),
            new WorkItemBuilder().WithId(250).WithType("Task").WithParentId(ChildA).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int>
        {
            ["root"] = 5,
            ["200"] = 3,
        }));
        // No PR for 200; PR for 250 with stale snapshot for both.
        StubPrListForBranch(runner, "plan/100-200");
        StubPrListForBranch(runner, "plan/100-250", (777, "plan/100-250", "https://gh/pr/777"));
        StubPrPoll(runner, 777, "plan/100-250",
            MakePrBodyWithSnapshot(new Dictionary<string, int>
            {
                ["root"] = 2,
                ["200"] = 1,
            }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalDescendantsScanned.ShouldBe(2);
        result.TotalStale.ShouldBe(1);
        var stale = result.StaleDescendants[0];
        stale.ItemId.ShouldBe(250);
        // Grandchild's immediate parent is ChildA (200), NOT the root.
        stale.ParentItemId.ShouldBe(ChildA);
        stale.StaleAncestors.Count.ShouldBe(2);
        // Sorted by key (Ordinal): "200" then "root".
        stale.StaleAncestors[0].AncestorKey.ShouldBe("200");
        stale.StaleAncestors[0].SnapshotGeneration.ShouldBe(1);
        stale.StaleAncestors[0].CurrentGeneration.ShouldBe(3);
        stale.StaleAncestors[1].AncestorKey.ShouldBe("root");
        stale.StaleAncestors[1].SnapshotGeneration.ShouldBe(2);
        stale.StaleAncestors[1].CurrentGeneration.ShouldBe(5);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_MultipleSiblings_OnlyStaleOnesReported()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build(),
            new WorkItemBuilder().WithId(ChildB).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 4 }));
        // ChildA PR is current; ChildB PR is stale.
        StubPrListForBranch(runner, "plan/100-200", (601, "plan/100-200", "https://gh/pr/601"));
        StubPrPoll(runner, 601, "plan/100-200",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 4 }));
        StubPrListForBranch(runner, "plan/100-300", (602, "plan/100-300", "https://gh/pr/602"));
        StubPrPoll(runner, 602, "plan/100-300",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalDescendantsScanned.ShouldBe(2);
        result.TotalDescendantsWithOpenPrs.ShouldBe(2);
        result.TotalStale.ShouldBe(1);
        result.StaleDescendants[0].ItemId.ShouldBe(ChildB);
        result.StaleDescendants[0].ParentItemId.ShouldBe(RootId);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_DeepGrandchildAndDirectChild_ParentItemIdsCorrect()
    {
        // Tree: root → 200 (direct child) → 250 (grandchild). Both PRs stale.
        // Verifies BFS parent tracking populates ParentItemId correctly for
        // each level — direct child's parent is root, grandchild's parent
        // is the intermediate ChildA.
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build(),
            new WorkItemBuilder().WithId(250).WithType("Task").WithParentId(ChildA).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 5 }));
        StubPrListForBranch(runner, "plan/100-200", (601, "plan/100-200", "https://gh/pr/601"));
        StubPrPoll(runner, 601, "plan/100-200",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));
        StubPrListForBranch(runner, "plan/100-250", (602, "plan/100-250", "https://gh/pr/602"));
        StubPrPoll(runner, 602, "plan/100-250",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalStale.ShouldBe(2);
        var byId = result.StaleDescendants.ToDictionary(s => s.ItemId, s => s.ParentItemId);
        byId[ChildA].ShouldBe(RootId);
        byId[250].ShouldBe(ChildA);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_PrSnapshotKeyMissingFromManifest_NotStale()
    {
        // PR snapshots an ancestor key that the manifest doesn't track.
        // Treat as not-stale (we cannot know the current generation —
        // surfacing every unknown key as stale would force human review
        // for every plan PR opened before its ancestors got recorded).
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 1 }));
        StubPrListForBranch(runner, "plan/100-200", (501, "plan/100-200", "https://gh/pr/501"));
        // Snapshot has key "999" which isn't in the manifest.
        StubPrPoll(runner, 501, "plan/100-200",
            MakePrBodyWithSnapshot(new Dictionary<string, int> { ["999"] = 1 }));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalStale.ShouldBe(0);
    }

    [Fact]
    public async Task ClassifyStaleDescendants_PrWithNoSnapshot_NotStale()
    {
        // Plan PR has no front-matter at all (older PR opened before P3
        // existed, or just a body without a fence).
        await SeedAsync(
            new WorkItemBuilder().WithId(RootId).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(ChildA).WithType("Issue").WithParentId(RootId).Build());
        var (cmd, runner) = CreateCommand();
        StubRemoteUrl(runner);
        StubGitShowManifest(runner, MakeManifest(new Dictionary<string, int> { ["root"] = 5 }));
        StubPrListForBranch(runner, "plan/100-200", (501, "plan/100-200", "https://gh/pr/501"));
        StubPrPoll(runner, 501, "plan/100-200", "Plain body, no front-matter");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClassifyStaleDescendants(RootId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.TotalDescendantsWithOpenPrs.ShouldBe(1);
        result.TotalStale.ShouldBe(0);
    }
}
