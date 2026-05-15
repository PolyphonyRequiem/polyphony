using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr merge-mg-pr</c>. Merges a merge-group PR into
/// its parent (parent MG when nested, feature branch when top-level).
/// Hardcodes merge-commit as the merge method per the branch-model ADR
/// (squash and rebase break the ancestry chain that nested MGs depend on).
/// Always preserves the head branch (sibling MGs may still be in flight).
/// </summary>
public sealed class PrCommandsMergeMgTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git), new Polyphony.Sdlc.Observers.RepoIdentityResolver(git)), runner);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubPrListOpen(FakeProcessRunner runner, int prNumber, string headRef)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("open"),
            (_, _) => Task.FromResult(new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"url":"https://x","headRefName":"{{headRef}}"}]""", "")));

    private static void StubPrListMerged(FakeProcessRunner runner, int prNumber, string headRef)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("merged"),
            (_, _) => Task.FromResult(new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"url":"https://x","headRefName":"{{headRef}}","mergedAt":"2026-05-06T00:00:00Z"}]""", "")));

    private static void StubPrListBothEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListOpenEmpty(FakeProcessRunner runner)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("open"),
            (_, _) => Task.FromResult(new ProcessResult(0, "[]", "")));

    private static void StubPrMergeOk(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));

    private static void StubPrViewMerged(FakeProcessRunner runner, string sha = "deadbeef")
        => runner.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0,
            $$"""{"number":42,"state":"MERGED","mergeCommit":{"oid":"{{sha}}"},"headRefName":"mg/100_core","headRefOid":"x"}""", ""));

    [Fact]
    public async Task MergeMgPr_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 0, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task MergeMgPr_InvalidMgPath_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "BAD!"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task MergeMgPr_TopLevelMg_BaseIsFeatureBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42, headRef: "mg/100_core");
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.HeadBranch.ShouldBe("mg/100_core");
        result.BaseBranch.ShouldBe("feature/100");
    }

    [Fact]
    public async Task MergeMgPr_NestedMg_BaseIsParentMgBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42, headRef: "mg/100_core_auth");
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core_auth"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.HeadBranch.ShouldBe("mg/100_core_auth");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task MergeMgPr_HappyPath_AlwaysUsesMergeMethodAndPreservesBranch()
    {
        // ADR docs/decisions/branch-model.md — MG PRs MUST use merge-commit
        // and MUST NOT delete the head branch (sibling MGs depend on it).
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42, headRef: "mg/100_core");
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.Method.ShouldBe("merge");
        result.DeleteBranch.ShouldBeFalse();

        var merge = runner.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--merge");
        merge.Arguments.ShouldNotContain("--squash");
        merge.Arguments.ShouldNotContain("--rebase");
        merge.Arguments.ShouldNotContain("--delete-branch");
    }

    [Fact]
    public async Task MergeMgPr_NoMatchingPr_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListBothEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.Error!.ShouldContain("no pull request");
    }

    [Fact]
    public async Task MergeMgPr_AlreadyMerged_ReturnsAlreadyMergedSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpenEmpty(runner);
        StubPrListMerged(runner, prNumber: 17, headRef: "mg/100_core");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMergeGroupResult)!;
        result.AlreadyMerged.ShouldBeTrue();
        result.Merged.ShouldBeTrue();
        result.PrNumber.ShouldBe(17);
        result.DeleteBranch.ShouldBeFalse();
        runner.Invocations.ShouldNotContain(i => i.Arguments[1] == "merge");
    }

    [Fact]
    public async Task MergeMgPr_AdminFlag_PassesAdminFlag()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42, headRef: "mg/100_core");
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        await CaptureConsoleAsync(() => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core", admin: true));

        runner.Invocations.Single(i => i.Arguments[1] == "merge").Arguments.ShouldContain("--admin");
    }

    [Fact]
    public async Task MergeMgPr_MatchHeadCommit_PassesFlagAndValue()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42, headRef: "mg/100_core");
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        await CaptureConsoleAsync(
            () => cmd.MergeMergeGroupPr(rootId: 100, mgPath: "core", matchHeadCommit: "cafef00d"));

        var merge = runner.Invocations.Single(i => i.Arguments[1] == "merge");
        var idx = merge.Arguments.ToList().IndexOf("--match-head-commit");
        idx.ShouldBeGreaterThan(0);
        merge.Arguments[idx + 1].ShouldBe("cafef00d");
    }
}
