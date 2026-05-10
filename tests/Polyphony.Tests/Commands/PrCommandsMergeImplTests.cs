using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr merge-impl-pr</c>. Merges the per-item task
/// PR into its enclosing merge-group branch. Default method is squash;
/// supports operator overrides via <c>--method</c>; idempotent when the
/// PR is already merged on the server.
/// </summary>
public sealed class PrCommandsMergeImplTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubPrListOpen(FakeProcessRunner runner, int prNumber)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("open"),
            (_, _) => Task.FromResult(new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"url":"https://x","headRefName":"impl/100-200"}]""", "")));

    private static void StubPrListMergedEmpty(FakeProcessRunner runner)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("merged"),
            (_, _) => Task.FromResult(new ProcessResult(0, "[]", "")));

    private static void StubPrListBothEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListMerged(FakeProcessRunner runner, int prNumber)
        => runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal)
                && a.Contains("merged"),
            (_, _) => Task.FromResult(new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"url":"https://x","headRefName":"impl/100-200","mergedAt":"2026-05-06T00:00:00Z"}]""", "")));

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
            $$"""{"number":42,"state":"MERGED","mergeCommit":{"oid":"{{sha}}"},"headRefName":"impl/100-200","headRefOid":"x"}""", ""));

    [Fact]
    public async Task MergeImplPr_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 0, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task MergeImplPr_InvalidItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 0, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task MergeImplPr_InvalidMgPath_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "BAD!"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task MergeImplPr_InvalidMethod_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core", method: "ff-only"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Error!.ShouldContain("merge method");
    }

    [Fact]
    public async Task MergeImplPr_NoMatchingPr_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListBothEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Error!.ShouldContain("no pull request");
        result.HeadBranch.ShouldBe("impl/100-200");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task MergeImplPr_HappyPath_DefaultSquashMergeReturnsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner, sha: "deadbeef");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.PrNumber.ShouldBe(42);
        result.Method.ShouldBe("squash");
        result.MergeSha.ShouldBe("deadbeef");
        result.DeleteBranch.ShouldBeTrue();

        var merge = runner.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--squash");
        merge.Arguments.ShouldContain("--delete-branch");
        merge.Arguments.ShouldNotContain("--admin");
        merge.Arguments.ShouldNotContain("--match-head-commit");
    }

    [Fact]
    public async Task MergeImplPr_OverrideMethodToMerge_PassesMergeFlag()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core", method: "merge"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.Method.ShouldBe("merge");
        runner.Invocations.Single(i => i.Arguments[1] == "merge").Arguments.ShouldContain("--merge");
    }

    [Fact]
    public async Task MergeImplPr_AlreadyMerged_ReturnsAlreadyMergedSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpenEmpty(runner);
        StubPrListMerged(runner, prNumber: 17);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.AlreadyMerged.ShouldBeTrue();
        result.Merged.ShouldBeTrue();
        result.PrNumber.ShouldBe(17);
        // No merge invocation issued — pure idempotent reconciliation.
        runner.Invocations.ShouldNotContain(i => i.Arguments[1] == "merge");
    }

    [Fact]
    public async Task MergeImplPr_AdminFlag_PassesAdminFlag()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core", admin: true));

        runner.Invocations.Single(i => i.Arguments[1] == "merge").Arguments.ShouldContain("--admin");
    }

    [Fact]
    public async Task MergeImplPr_DeleteBranchFalse_OmitsDeleteFlag()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core", deleteBranch: false));

        var merge = runner.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldNotContain("--delete-branch");
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.DeleteBranch.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeImplPr_MatchHeadCommit_PassesFlagAndValue()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core", matchHeadCommit: "abc123"));

        var merge = runner.Invocations.Single(i => i.Arguments[1] == "merge");
        var idx = merge.Arguments.ToList().IndexOf("--match-head-commit");
        idx.ShouldBeGreaterThan(0);
        merge.Arguments[idx + 1].ShouldBe("abc123");
    }

    [Fact]
    public async Task MergeImplPr_NestedMgPath_ResolvesNestedBaseBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/o/r.git");
        StubPrListOpen(runner, prNumber: 42);
        StubPrMergeOk(runner);
        StubPrViewMerged(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplPr(rootId: 100, itemId: 200, mgPath: "core_auth"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplResult)!;
        result.HeadBranch.ShouldBe("impl/100-200");
        result.BaseBranch.ShouldBe("mg/100_core_auth");
        result.MgPath.ShouldBe("core_auth");
    }
}
