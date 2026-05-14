using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr assert-impl-pr-coverage</c> — post-squash
/// assertion that the MG-branch squash commit carries the cumulative
/// diff of the source impl branch. Defends against AB#3211 (silent
/// commit drop on squash merge, observed in apex 3165 where
/// <c>mg/3165_pg-3176</c> received only 1 of 3 commits' content).
/// </summary>
public sealed class PrCommandsAssertImplPrCoverageTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner);
    }

    private static void StubDiff(FakeProcessRunner runner, string fromRef, string toRef, string diff)
        => runner.WhenExact("git", ["diff", "--no-color", $"{fromRef}..{toRef}"],
            new ProcessResult(0, diff, ""));

    private static void StubDiffFails(FakeProcessRunner runner, string fromRef, string toRef, int exit, string stderr)
        => runner.WhenExact("git", ["diff", "--no-color", $"{fromRef}..{toRef}"],
            new ProcessResult(exit, "", stderr));

    private static void StubRevList(FakeProcessRunner runner, string range, params (string Sha, string Subject)[] commits)
    {
        var sb = new StringBuilder();
        foreach (var (sha, subject) in commits)
        {
            // git rev-list --pretty=tformat:... interleaves a bare SHA
            // line per commit ahead of the formatted record.
            sb.Append(sha).Append('\n');
            sb.Append(sha).Append('\t').Append(subject).Append('\n');
        }
        runner.WhenExact("git",
            ["rev-list", "--reverse", "--pretty=tformat:%H%x09%s", range],
            new ProcessResult(0, sb.ToString(), ""));
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [Fact]
    public async Task AssertImplPrCoverage_MissingRootId_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(itemId: 200, mgPath: "pg-200"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task AssertImplPrCoverage_MissingItemId_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, mgPath: "pg-200"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task AssertImplPrCoverage_MissingMgPath_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task AssertImplPrCoverage_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 0, itemId: 200, mgPath: "pg-200"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task AssertImplPrCoverage_InvalidMgPath_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200, mgPath: "not a valid path"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task AssertImplPrCoverage_DiffsMatch_EmitsOk()
    {
        var (cmd, runner) = CreateCommand();
        const string sharedDiff = "diff --git a/foo.cs b/foo.cs\n+hello\n";
        StubDiff(runner, "origin/mg/100_pg-200^", "origin/impl/100-200", sharedDiff);
        StubDiff(runner, "origin/mg/100_pg-200^", "origin/mg/100_pg-200", sharedDiff);
        StubRevList(runner, "origin/mg/100_pg-200^..origin/impl/100-200",
            ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "feat: hello"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200, mgPath: "pg-200"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("ok");
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(200);
        result.MgPath.ShouldBe("pg-200");
        result.ImplRef.ShouldBe("origin/impl/100-200");
        result.MgRef.ShouldBe("origin/mg/100_pg-200");
        result.ComparisonBase.ShouldBe("origin/mg/100_pg-200^");
        result.ExpectedDiffHash.ShouldBe(Sha256Hex(sharedDiff));
        result.ActualDiffHash.ShouldBe(Sha256Hex(sharedDiff));
        result.ExpectedDiffBytes.ShouldBe(Encoding.UTF8.GetByteCount(sharedDiff));
        result.ActualDiffBytes.ShouldBe(Encoding.UTF8.GetByteCount(sharedDiff));
        result.ImplBranchCommits.Count.ShouldBe(1);
        result.ImplBranchCommits[0].Sha.ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        result.ImplBranchCommits[0].Subject.ShouldBe("feat: hello");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task AssertImplPrCoverage_DiffsDiverge_EmitsMismatchAndListsCommits()
    {
        // The exact AB#3211 reproducer in miniature: impl branch carries 3
        // commits' worth of changes; squash on mg only carries 1 of them.
        var (cmd, runner) = CreateCommand();
        const string fullImplDiff =
            "diff --git a/zero.cs b/zero.cs\n+verb\n" +
            "diff --git a/scenario.yaml b/scenario.yaml\n+yaml\n" +
            "diff --git a/quote-fix.yaml b/quote-fix.yaml\n+quote\n";
        const string partialSquash =
            "diff --git a/scenario.yaml b/scenario.yaml\n+yaml\n";
        StubDiff(runner, "origin/mg/3165_pg-3176^", "origin/impl/3165-3176", fullImplDiff);
        StubDiff(runner, "origin/mg/3165_pg-3176^", "origin/mg/3165_pg-3176", partialSquash);
        StubRevList(runner, "origin/mg/3165_pg-3176^..origin/impl/3165-3176",
            ("3f45269000000000000000000000000000000000", "feat: branch-tip zero-diff short-circuit AB#3175"),
            ("99f817e000000000000000000000000000000000", "fix: quote output_contains satisfied as string AB#3176"),
            ("7d811fe000000000000000000000000000000000", "Add idempotent rerun short-circuit harness scenario AB#3176"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 3165, itemId: 3176, mgPath: "pg-3176"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("mismatch");
        result.ExpectedDiffHash.ShouldBe(Sha256Hex(fullImplDiff));
        result.ActualDiffHash.ShouldBe(Sha256Hex(partialSquash));
        result.ExpectedDiffBytes.ShouldBe(Encoding.UTF8.GetByteCount(fullImplDiff));
        result.ActualDiffBytes.ShouldBe(Encoding.UTF8.GetByteCount(partialSquash));
        result.ExpectedDiffBytes.ShouldBeGreaterThan(result.ActualDiffBytes);
        result.ImplBranchCommits.Count.ShouldBe(3);
        result.ImplBranchCommits[0].Sha.ShouldStartWith("3f45269");
        result.ImplBranchCommits[0].Subject.ShouldContain("AB#3175");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task AssertImplPrCoverage_GitDiffFails_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        StubDiffFails(runner, "origin/mg/100_pg-200^", "origin/impl/100-200",
            128, "fatal: bad revision 'origin/mg/100_pg-200^'");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200, mgPath: "pg-200"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("bad revision");
    }

    [Fact]
    public async Task AssertImplPrCoverage_NestedMgPath_ResolvesCorrectly()
    {
        // Exercises nested merge groups: pg-200_core resolves to
        // mg/100_pg-200_core. Comparison base, refs, and exit code all
        // wired through correctly.
        var (cmd, runner) = CreateCommand();
        const string diff = "diff --git a/x b/x\n+y\n";
        StubDiff(runner, "origin/mg/100_pg-200_core^", "origin/impl/100-300", diff);
        StubDiff(runner, "origin/mg/100_pg-200_core^", "origin/mg/100_pg-200_core", diff);
        StubRevList(runner, "origin/mg/100_pg-200_core^..origin/impl/100-300");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 300, mgPath: "pg-200_core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("ok");
        result.MgRef.ShouldBe("origin/mg/100_pg-200_core");
        result.ImplRef.ShouldBe("origin/impl/100-300");
    }

    [Fact]
    public async Task AssertImplPrCoverage_CustomRemote_ThreadsThrough()
    {
        var (cmd, runner) = CreateCommand();
        const string diff = "+x\n";
        StubDiff(runner, "ado/mg/100_pg-200^", "ado/impl/100-200", diff);
        StubDiff(runner, "ado/mg/100_pg-200^", "ado/mg/100_pg-200", diff);
        StubRevList(runner, "ado/mg/100_pg-200^..ado/impl/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200, mgPath: "pg-200", remote: "ado"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult)!;
        result.Action.ShouldBe("ok");
        result.ImplRef.ShouldBe("ado/impl/100-200");
        result.MgRef.ShouldBe("ado/mg/100_pg-200");
    }

    [Fact]
    public async Task AssertImplPrCoverage_OutputUsesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        const string diff = "+x\n";
        StubDiff(runner, "origin/mg/100_pg-200^", "origin/impl/100-200", diff);
        StubDiff(runner, "origin/mg/100_pg-200^", "origin/mg/100_pg-200", diff);
        StubRevList(runner, "origin/mg/100_pg-200^..origin/impl/100-200");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.AssertImplPrCoverage(rootId: 100, itemId: 200, mgPath: "pg-200"));
        // JsonContext serializes via SnakeCaseLower converter; verify the
        // wire shape matches what the workflow lint expects.
        output.ShouldContain("\"action\":");
        output.ShouldContain("\"root_id\":");
        output.ShouldContain("\"item_id\":");
        output.ShouldContain("\"mg_path\":");
        output.ShouldContain("\"impl_ref\":");
        output.ShouldContain("\"mg_ref\":");
        output.ShouldContain("\"comparison_base\":");
        output.ShouldContain("\"expected_diff_hash\":");
        output.ShouldContain("\"actual_diff_hash\":");
        output.ShouldContain("\"expected_diff_bytes\":");
        output.ShouldContain("\"actual_diff_bytes\":");
        output.ShouldContain("\"impl_branch_commits\":");
    }
}
