using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr open-evidence-pr</c> — the Phase 6 verb that
/// opens (or reuses) a GitHub PR promoting an evidence branch into its
/// parent feature branch (or <c>main</c> for the orphan-evidence case).
///
/// Mirrors the structure of <see cref="PrCommandsTests"/> (create-feature-pr)
/// and <see cref="PrCommandsOpenMgPrTests"/> (open-mg-pr): in-process
/// <see cref="FakeProcessRunner"/>, no real <c>gh</c> shell-out, and a
/// per-verb JSON-contract smoke test.
/// </summary>
public sealed class PrOpenEvidenceTests : CommandTestBase
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

    private static void StubLsRemoteHas(FakeProcessRunner runner, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubTwigShowTree(FakeProcessRunner runner, int id, string? title)
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

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    private static void StubPrCreateFailure(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(1, "", "boom"));

    private static void StubAllRemoteHeadsExist(
        FakeProcessRunner runner,
        string headBranch,
        string baseBranch)
    {
        StubLsRemoteHas(runner, $"refs/heads/{headBranch}", exists: true);
        StubLsRemoteHas(runner, $"refs/heads/{baseBranch}", exists: true);
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_NonPositiveWorkItem_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenEvidencePr(workItem: 0));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("workItem");
        result.Created.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenEvidencePr_NegativeApexId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenEvidencePr(workItem: 123, apexId: -1));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Error!.ShouldContain("apexId");
    }

    // ─── Branch naming defaults ──────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_HappyPath_ApexProvided_UsesDashedHeadAndFeatureBase()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Talk to security about MFA");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/77");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(77);
        result.PrUrl.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/77");
        result.HeadBranch.ShouldBe("evidence/100-123");
        result.BaseBranch.ShouldBe("feature/100");
        result.WorkItemId.ShouldBe(123);
        result.ApexId.ShouldBe(100);
        result.Title.ShouldBe("Evidence: Talk to security about MFA (#123)");
    }

    [Fact]
    public async Task OpenEvidencePr_HappyPath_NoApex_CollapsesToOrphanFormAgainstMain()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/123", "main");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Standalone investigation");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/78");

        // No --apex-id supplied — apex should default to workItem (orphan case).
        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenEvidencePr(workItem: 123));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Created.ShouldBeTrue();
        result.HeadBranch.ShouldBe("evidence/123");
        result.BaseBranch.ShouldBe("main");
        result.WorkItemId.ShouldBe(123);
        result.ApexId.ShouldBe(123); // collapsed: apex == work item
    }

    [Fact]
    public async Task OpenEvidencePr_ApexEqualsWorkItem_StillCollapsesToOrphanForm()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/123", "main");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Self-apex item");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/79");

        // Explicit --apex-id 123 with workItem=123 also collapses.
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 123));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.HeadBranch.ShouldBe("evidence/123");
        result.BaseBranch.ShouldBe("main");
    }

    // ─── Override knobs ──────────────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_CustomHeadAndBase_HonoredVerbatim()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "custom/head-branch", "release/v2");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Custom flow");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/80");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(
                workItem: 123,
                apexId: 100,
                head: "custom/head-branch",
                baseBranch: "release/v2"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.HeadBranch.ShouldBe("custom/head-branch");
        result.BaseBranch.ShouldBe("release/v2");
    }

    [Fact]
    public async Task OpenEvidencePr_TitleAndBodySuppliedExplicitly_OverrideTwigComposition()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        // Twig is ALSO stubbed to a competing title so we prove the override wins.
        StubTwigShowTree(runner, 123, "Twig title that should be ignored");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/81");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(
                workItem: 123,
                apexId: 100,
                title: "Explicit evidence title",
                body: "Explicit body content"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Title.ShouldBe("Explicit evidence title");

        // The explicit body must have made it onto the gh pr create call.
        var createInvocation = runner.Invocations
            .First(i => i.Executable == "gh" && i.Arguments.Count >= 2
                && i.Arguments[0] == "pr" && i.Arguments[1] == "create");
        createInvocation.Arguments.ShouldContain("Explicit body content");
    }

    [Fact]
    public async Task OpenEvidencePr_TwigShowTreeFails_FallsBackToGenericTitle()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, title: null); // simulates non-zero exit
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/82");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Title.ShouldBe("Evidence for #123");
    }

    // ─── Idempotency ─────────────────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_ExistingOpenPr_ReusesItWithoutCreatingDuplicate()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Reused");
        StubPrListExisting(runner, 50, "https://github.com/PolyphonyRequiem/polyphony/pull/50",
            headRef: "evidence/100-123");
        // No StubPrCreate — must NOT be called.

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(50);
        result.PrUrl.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/50");

        var createCalled = runner.Invocations.Any(i =>
            i.Executable == "gh" && i.Arguments.Count >= 2
            && i.Arguments[0] == "pr" && i.Arguments[1] == "create");
        createCalled.ShouldBeFalse();
    }

    // ─── Error envelopes ─────────────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_HeadMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/evidence/100-123", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Error!.ShouldContain("head branch");
        result.HeadBranch.ShouldBe("evidence/100-123");
    }

    [Fact]
    public async Task OpenEvidencePr_BaseMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/evidence/100-123", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Error!.ShouldContain("base branch");
    }

    [Fact]
    public async Task OpenEvidencePr_NoSlug_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(1, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Error!.ShouldContain("repo slug");
    }

    [Fact]
    public async Task OpenEvidencePr_GhCreateFails_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Title");
        StubPrListEmpty(runner);
        StubPrCreateFailure(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Created.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task OpenEvidencePr_GhCreateAllAttemptsTimeout_ReconcileMisses_ReturnsRoutingFailure()
    {
        // Tight policy so the test stays fast: per-attempt 50 ms, two
        // attempts, no backoff. Mirrors the GhClientRetryTests pattern.
        var policy = new GhClientPolicy(
            maxAttempts: 2,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);

        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner, policy);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git));

        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Timeout case");

        // pr list is exercised twice: the verb's pre-create reuse check
        // (first call, returns []) AND the GhClient's reconcile-by-list
        // path after each timed-out create (also []). A single open-ended
        // responder covers both.
        StubPrListEmpty(runner);

        // pr create hangs forever — every attempt is killed by the
        // per-attempt timeout. With reconcile returning [] each time,
        // GhClient ultimately throws ExternalToolTimeoutException, which
        // the verb catches and surfaces as a routable error envelope.
        runner.WhenAsync(
            (e, a) => e == "gh" && a.Count >= 2 && a[0] == "pr" && a[1] == "create",
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProcessResult(0, "", "");
            });

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenEvidenceResult)!;
        result.Created.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
        // Honest report: command surfaces the timeout in the envelope
        // instead of pretending the PR was created.
        result.PrNumber.ShouldBe(0);
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task OpenEvidencePr_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubAllRemoteHeadsExist(runner, "evidence/100-123", "feature/100");
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 123, "Snake case me");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/91");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenEvidencePr(workItem: 123, apexId: 100));

        output.ShouldContain("\"pr_number\"", Case.Sensitive);
        output.ShouldContain("\"pr_url\"", Case.Sensitive);
        output.ShouldContain("\"title\"", Case.Sensitive);
        output.ShouldContain("\"head_branch\"", Case.Sensitive);
        output.ShouldContain("\"base_branch\"", Case.Sensitive);
        output.ShouldContain("\"work_item_id\"", Case.Sensitive);
        output.ShouldContain("\"apex_id\"", Case.Sensitive);
        output.ShouldContain("\"created\"", Case.Sensitive);
        output.ShouldNotContain("\"PrNumber\"", Case.Sensitive);
        output.ShouldNotContain("\"WorkItemId\"", Case.Sensitive);
    }
}
