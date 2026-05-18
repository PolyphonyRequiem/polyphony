using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Round-trip tests for the <c>polyphony reset</c> verb family —
/// <c>reset state</c>, <c>reset prs</c>, <c>reset branches</c>,
/// <c>reset worktrees</c>, <c>reset manifest</c>, and the
/// <c>reset apex</c> composite.
///
/// <para>Mirrors the stubbing pattern from
/// <see cref="BranchCommandsMarkImplMergedTests"/>: real
/// <see cref="TwigClient"/> / <see cref="GitClient"/> / <see cref="GhClient"/>
/// over a <see cref="FakeProcessRunner"/>, so the verbs exercise the
/// same shell-out boundary they hit in production.</para>
/// </summary>
public sealed class ResetCommandsTests : CommandTestBase
{
    private (ResetCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);

        var cmd = new ResetCommands(twig, git, pullRequestReader, planObserver, walker);
        return (cmd, runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    /// <summary>
    /// Stubs <c>twig show {id}</c> + <c>twig patch --id {id}</c> as a
    /// round-trip — mirrors the helper in
    /// BranchCommandsMarkImplMergedTests so reset state observes the
    /// patched tag through twig the same way production does.
    /// </summary>
    private static void StubTagsRoundTrip(FakeProcessRunner runner, int workItemId, string initialTags)
    {
        var state = new[] { initialTags };

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 4
                && a[0] == "show"
                && a[1] == workItemId.ToString()
                && a[^1] == "json",
            (_, _) =>
            {
                var encoded = JsonEncodedText.Encode(state[0]).Value;
                var json = $$"""{"id":{{workItemId}},"tags":"{{encoded}}"}""";
                return Task.FromResult(new ProcessResult(0, json, ""));
            });

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 5
                && a[0] == "patch"
                && a[1] == "--id"
                && a[2] == workItemId.ToString()
                && a[3] == "--json",
            (args, _) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args[4]);
                    if (doc.RootElement.TryGetProperty("System.Tags", out var tagsEl))
                    {
                        state[0] = tagsEl.GetString() ?? state[0];
                    }
                }
                catch (JsonException) { /* fall through */ }
                return Task.FromResult(new ProcessResult(0, "{}", ""));
            });
    }

    /// <summary>
    /// Stub <c>git remote get-url origin</c> + <c>git rev-parse</c> so
    /// PlanObserver.TryResolveRepoIdentityAsync resolves to a GitHub repo
    /// without needing a real working tree.
    /// </summary>
    private static void StubGitHubIdentity(FakeProcessRunner runner, string slug = "owner/repo")
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, $"https://github.com/{slug}.git\n", ""));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/fake/.git\n", ""));
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"],
            new ProcessResult(0, "C:/fake\n", ""));
    }

    // ---------- reset state -----------------------------------------------

    [Fact]
    public async Task ResetState_MissingApex_Halts()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(() => cmd.ResetState());
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task ResetState_DryRun_DoesNotInvokePatch()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Apex.ShouldBe(100);
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.NewWatermark.ShouldNotBeNullOrEmpty();
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetState_Execute_StampsWatermarkAndReportsPrevious()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:run-started-at=2024-01-01T00:00:00.000Z");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeFalse();
        result.PreviousWatermark.ShouldStartWith("2024-01-01T00:00:00");
        // New watermark must be a parseable ISO-8601 instant.
        DateTimeOffset.Parse(result.NewWatermark!).ShouldBeGreaterThan(DateTimeOffset.MinValue);
        // Verify the patch was actually issued.
        runner.Invocations.ShouldContain(i =>
            i.Executable == "twig"
            && i.Arguments.Count > 0
            && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetState_DuplicateTags_StripsAllButOne()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:run-started-at=2024-01-01T00:00:00.000Z; polyphony:run-started-at=2024-02-01T00:00:00.000Z");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        // Two original tags → strip them all, leave one fresh. Duplicates removed = total - 1.
        result.RemovedDuplicateTags.ShouldBe(1);
    }

    // ---------- reset branches --------------------------------------------

    [Fact]
    public async Task ResetBranches_DryRun_DoesNotInvokeDelete()
    {
        var (cmd, runner) = CreateCommand();
        // Stub ls-remote to return one match for plan/100.
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 3 && a[0] == "ls-remote" && a[1] == "--heads",
            (a, _) =>
            {
                // a[2]="origin", a[3]=pattern.
                var pattern = a.Count > 3 ? a[3] : string.Empty;
                if (pattern == "refs/heads/plan/100")
                {
                    return Task.FromResult(new ProcessResult(0,
                        "deadbeef\trefs/heads/plan/100\n", ""));
                }
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetBranches(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetBranchesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.DeletedBranches.Count.ShouldBe(1);
        result.DeletedBranches[0].Branch.ShouldBe("plan/100");
        result.DeletedBranches[0].DeletedRemote.ShouldBeTrue();
        // No actual delete commands.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] == "branch" || (i.Arguments[0] == "push" && i.Arguments.Contains("--delete"))));
    }

    // ---------- reset worktrees -------------------------------------------

    [Fact]
    public async Task ResetWorktrees_NoWorktreesUnderApex_SucceedsWithEmptyList()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/repo/.git\n", ""));
        // No worktrees at all → filter yields empty regardless of root path.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetWorktrees(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetWorktreesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.RemovedWorktrees.Count.ShouldBe(0);
        result.ApexRunsRoot.ShouldEndWith("apex-100");
    }

    // ---------- reset manifest --------------------------------------------

    [Fact]
    public async Task ResetManifest_FeatureBranchAbsent_ReportsAbsent()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetManifest(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetManifestResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.FeatureBranch.ShouldBe("feature/100");
        result.FeatureBranchExists.ShouldBeFalse();
        result.ManifestPresent.ShouldBeFalse();
        result.DeferralReason.ShouldNotBeNullOrEmpty();
    }

    // ---------- reset prs -------------------------------------------------

    [Fact]
    public async Task ResetPrs_NoOpenPrs_ReportsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubGitHubIdentity(runner);
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetPrs(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetPrsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.AbandonedPrs.Count.ShouldBe(0);
        result.FailedPrs.Count.ShouldBe(0);
    }

    // ---------- reset apex composite --------------------------------------

    [Fact]
    public async Task ResetApex_SkipState_OmitsStateStep()
    {
        var (cmd, runner) = CreateCommand();
        StubGitHubIdentity(runner);
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/repo/.git\n", ""));
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetApex(apex: 100, execute: false, skipState: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetApexResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.StateSkipped.ShouldBeTrue();
        result.StepsCompleted.ShouldNotContain("state");
        result.State.ShouldBeNull();
        // Other steps ran in order.
        result.StepsCompleted.ShouldContain("prs");
        result.StepsCompleted.ShouldContain("worktrees");
        result.StepsCompleted.ShouldContain("branches");
        result.StepsCompleted.ShouldContain("manifest");
        // Composite is dry-run end-to-end.
        result.DryRun.ShouldBeTrue();
        result.Prs!.DryRun.ShouldBeTrue();
        result.Worktrees!.DryRun.ShouldBeTrue();
        result.Branches!.DryRun.ShouldBeTrue();
    }
}
