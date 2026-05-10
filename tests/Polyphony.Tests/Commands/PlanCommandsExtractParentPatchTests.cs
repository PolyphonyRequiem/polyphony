using System.Text;
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
/// Tests for <c>polyphony plan extract-parent-patch</c>. Composes
/// minimal stubs for <c>gh pr view</c> + <c>gh pr diff</c> and asserts
/// the emitted JSON. Always exits 0 (routing-style verb).
/// </summary>
public sealed class PlanCommandsExtractParentPatchTests : CommandTestBase
{
    private const int RootId = 100;
    private const int ParentItemId = 200;
    private const string PrUrl = "https://github.com/acme/repo/pull/77";
    private const int PrNumber = 77;

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(new GitClient(runner))), runner);
    }

    private static PlanExtractParentPatchResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanExtractParentPatchResult)!;

    private static void StubPrView(
        FakeProcessRunner runner,
        int prNumber,
        string body,
        string headRef = "plan/100-300",
        string headSha = "abc123")
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "OPEN",
              "reviewDecision": "APPROVED",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRef}}",
              "headRefOid": "{{headSha}}",
              "baseRefName": "plan/100",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubPrDiff(FakeProcessRunner runner, int prNumber, string diff)
        => runner.WhenExact("gh", ["pr", "diff", prNumber.ToString(), "--repo", "acme/repo"],
            new ProcessResult(0, diff, ""));

    private static string MakeBody(bool requestsParentChange, IDictionary<string, int>? snapshot = null)
    {
        var lines = new List<string>
        {
            "---",
            $"requests_parent_change: {(requestsParentChange ? "true" : "false")}",
            "ancestor_plan_generations:",
        };
        foreach (var (k, v) in snapshot ?? new Dictionary<string, int>())
        {
            lines.Add($"  \"{k}\": {v}");
        }
        lines.Add("---");
        lines.Add("");
        lines.Add("PR description body.");
        return string.Join("\n", lines);
    }

    private static string MakeDiffForFile(string path, string body)
        => $"diff --git a/{path} b/{path}\nindex aaaa..bbbb 100644\n--- a/{path}\n+++ b/{path}\n@@ -1,3 +1,4 @@\n {body}\n";

    // ─── Argument validation ──────────────────────────────────────────────

    [Fact]
    public async Task ExtractParentPatch_NegativeRootId_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, rootId: -1, parentItemId: ParentItemId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("invalid_root_id");
    }

    [Fact]
    public async Task ExtractParentPatch_ZeroParentItemId_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, parentItemId: 0));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("invalid_parent_item_id");
    }

    [Fact]
    public async Task ExtractParentPatch_BadPrUrl_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch("not-a-url", RootId, ParentItemId));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("invalid_pr_url");
    }

    [Fact]
    public async Task ExtractParentPatch_NegativeSizeLimit_EmitsError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId, diffSizeLimitBytes: -1));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("invalid_size_limit");
    }

    // ─── PR fetch failures ────────────────────────────────────────────────

    [Fact]
    public async Task ExtractParentPatch_PrNotFound_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        // gh pr view returns null when exit code != 0.
        runner.WhenStartsWith("gh", ["pr", "view", PrNumber.ToString()],
            new ProcessResult(1, "", "no such PR"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task ExtractParentPatch_DiffUnavailable_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, MakeBody(true, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }));
        runner.WhenExact("gh", ["pr", "diff", PrNumber.ToString(), "--repo", "acme/repo"],
            new ProcessResult(1, "", "diff unavailable"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.ErrorCode.ShouldBe("diff_unavailable");
    }

    // ─── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractParentPatch_HappyPath_ExtractsParentPlanHunks()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{ParentItemId}.md";
        var unrelatedDiff = MakeDiffForFile("plans/plan-300.md", "child plan content");
        var parentDiff = MakeDiffForFile(parentPlanPath, "parent plan content");

        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }),
            headRef: "plan/100-300", headSha: "deadbeef");
        StubPrDiff(runner, PrNumber, unrelatedDiff + parentDiff);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.PrNumber.ShouldBe(PrNumber);
        result.RepoSlug.ShouldBe("acme/repo");
        result.ParentItemId.ShouldBe(ParentItemId);
        result.ChildItemId.ShouldBe(300);
        result.HeadSha.ShouldBe("deadbeef");
        result.RequestsParentChange.ShouldBeTrue();
        result.ExpectedParentGeneration.ShouldBe(1);
        result.FilesTouched.ShouldHaveSingleItem();
        result.FilesTouched[0].ShouldBe(parentPlanPath);
        result.ParentPlanDiff.ShouldContain($"diff --git a/{parentPlanPath}");
        result.ParentPlanDiff.ShouldContain("parent plan content");
        result.ParentPlanDiff.ShouldNotContain("plans/plan-300.md");
        result.Truncated.ShouldBeFalse();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractParentPatch_RootParent_UsesRootSnapshotKey()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{RootId}.md";
        var diff = MakeDiffForFile(parentPlanPath, "root plan body");

        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { ["root"] = 5 }),
            headRef: "plan/100-300");
        StubPrDiff(runner, PrNumber, diff);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, parentItemId: RootId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ExpectedParentGeneration.ShouldBe(5);
        result.Warnings.ShouldBeEmpty();
    }

    // ─── Warnings (non-error degraded output) ─────────────────────────────

    [Fact]
    public async Task ExtractParentPatch_FlagFalse_WarnsButStillEmits()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{ParentItemId}.md";
        StubPrView(runner, PrNumber,
            MakeBody(false, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }));
        StubPrDiff(runner, PrNumber, MakeDiffForFile(parentPlanPath, "x"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.RequestsParentChange.ShouldBeFalse();
        result.Warnings.ShouldNotBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("requests_parent_change"));
    }

    [Fact]
    public async Task ExtractParentPatch_NoParentFileInDiff_WarnsAndEmptyFilesTouched()
    {
        var (cmd, runner) = CreateCommand();
        var unrelatedDiff = MakeDiffForFile("plans/plan-999.md", "unrelated");
        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }));
        StubPrDiff(runner, PrNumber, unrelatedDiff);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.FilesTouched.ShouldBeEmpty();
        result.ParentPlanDiff.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("does not touch"));
    }

    [Fact]
    public async Task ExtractParentPatch_SnapshotKeyMissing_WarnsExpectedParentGenNull()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{ParentItemId}.md";
        // Snapshot omits the parent's key.
        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { ["999"] = 1 }));
        StubPrDiff(runner, PrNumber, MakeDiffForFile(parentPlanPath, "x"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ExpectedParentGeneration.ShouldBeNull();
        result.Warnings.ShouldContain(w => w.Contains($"'{ParentItemId}'"));
    }

    [Fact]
    public async Task ExtractParentPatch_UnrecognizedHeadRef_WarnsChildIdUnknown()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{ParentItemId}.md";
        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }),
            headRef: "some/random/branch");
        StubPrDiff(runner, PrNumber, MakeDiffForFile(parentPlanPath, "x"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ChildItemId.ShouldBeNull();
        result.Warnings.ShouldContain(w => w.Contains("plan branch"));
    }

    // ─── Truncation ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractParentPatch_LargeDiff_TruncatesAndAppendsNotice()
    {
        var (cmd, runner) = CreateCommand();
        var parentPlanPath = $"plans/plan-{ParentItemId}.md";
        var bigBody = new string('x', 100 * 1024);  // 100 KB > 1 KB cap
        StubPrView(runner, PrNumber,
            MakeBody(true, new Dictionary<string, int> { [ParentItemId.ToString()] = 1 }));
        StubPrDiff(runner, PrNumber, MakeDiffForFile(parentPlanPath, bigBody));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractParentPatch(PrUrl, RootId, ParentItemId, diffSizeLimitBytes: 1024));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Truncated.ShouldBeTrue();
        result.DiffSizeBytes.ShouldBeGreaterThan(1024);
        Encoding.UTF8.GetByteCount(result.ParentPlanDiff).ShouldBeLessThanOrEqualTo(1024);
        result.ParentPlanDiff.ShouldContain("truncated by polyphony plan extract-parent-patch");
    }
}
