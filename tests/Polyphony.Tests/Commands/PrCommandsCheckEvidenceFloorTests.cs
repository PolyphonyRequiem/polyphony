using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr check-evidence-floor</c> — the Phase 6 PR #7
/// strict mechanical pre-reviewer gate (≥1 commit + non-empty body).
///
/// <para>Mirrors <see cref="PrOpenEvidenceTests"/>: in-process
/// <see cref="FakeProcessRunner"/>, no real <c>gh</c> shell-out, and a
/// per-verb JSON-contract smoke test. The verb is routing-style — every
/// outcome (pass / violation / transport failure) emits exit 0 with the
/// distinction in the JSON envelope.</para>
/// </summary>
public sealed class PrCommandsCheckEvidenceFloorTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (
            new PrCommands(
                git, gh, twig, Repository, Config,
                new Polyphony.Locking.RunLockStore(),
                new Polyphony.Locking.RunLockPathResolver(git)),
            runner);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubPrViewFloor(
        FakeProcessRunner runner,
        int prNumber,
        int commitCount,
        string body)
    {
        var commits = string.Join(",", Enumerable.Range(0, commitCount)
            .Select(i => $"{{\"oid\":\"sha{i:D2}\"}}"));
        var json = $"{{\"commits\":[{commits}],\"body\":{JsonSerializer.Serialize(body)}}}";
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, json, ""));
    }

    private static void StubPrViewFailure(
        FakeProcessRunner runner,
        int prNumber,
        int exitCode,
        string stderr)
    {
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(exitCode, "", stderr));
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task CheckEvidenceFloor_NonPositivePrNumber_EmitsGhFailedEnvelope()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 0));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.PassesFloor.ShouldBeFalse();
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("prNumber");
    }

    [Fact]
    public async Task CheckEvidenceFloor_NegativeMinCommits_EmitsGhFailedEnvelope()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 7, minCommits: -1));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("minCommits");
    }

    [Fact]
    public async Task CheckEvidenceFloor_NoSlugResolvable_EmitsGhFailedEnvelope()
    {
        var (cmd, runner) = CreateCommand();
        // Origin lookup fails — no --repo override either.
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(1, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 7));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.PassesFloor.ShouldBeFalse();
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("repo slug");
    }

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task CheckEvidenceFloor_HasCommitsAndBody_PassesFloor()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 3, body: "Some real evidence here.");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeTrue();
        result.PrNumber.ShouldBe(42);
        result.CommitCount.ShouldBe(3);
        result.BodyLength.ShouldBe("Some real evidence here.".Length);
        result.PassesFloor.ShouldBeTrue();
        result.Violations.ShouldBeEmpty();
        result.ErrorCode.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task CheckEvidenceFloor_RepoOverride_BypassesSlugResolution()
    {
        var (cmd, runner) = CreateCommand();
        // No StubGitRemoteOrigin — proves the verb does NOT call git when --repo is supplied.
        StubPrViewFloor(runner, prNumber: 42, commitCount: 1, body: "ok");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CheckEvidenceFloor(prNumber: 42, repo: "explicit/repo"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.PassesFloor.ShouldBeTrue();

        var viewInvocation = runner.Invocations
            .First(i => i.Executable == "gh" && i.Arguments.Count >= 2
                && i.Arguments[0] == "pr" && i.Arguments[1] == "view");
        viewInvocation.Arguments.ShouldContain("explicit/repo");

        var gitCalled = runner.Invocations.Any(i => i.Executable == "git");
        gitCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckEvidenceFloor_MinCommitsTwo_OneCommit_FailsWithNoCommits()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 1, body: "fine");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CheckEvidenceFloor(prNumber: 42, minCommits: 2));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.PassesFloor.ShouldBeFalse();
        result.Violations.ShouldContain("no_commits");
        result.Violations.ShouldNotContain("empty_body");
    }

    // ─── Violations ──────────────────────────────────────────────────────

    [Fact]
    public async Task CheckEvidenceFloor_ZeroCommits_EmitsNoCommitsViolation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 0, body: "Body is fine.");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeTrue();
        result.PassesFloor.ShouldBeFalse();
        result.CommitCount.ShouldBe(0);
        result.Violations.Count.ShouldBe(1);
        result.Violations[0].ShouldBe("no_commits");
    }

    [Fact]
    public async Task CheckEvidenceFloor_EmptyBody_EmitsEmptyBodyViolation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 2, body: "");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeTrue();
        result.PassesFloor.ShouldBeFalse();
        result.BodyLength.ShouldBe(0);
        result.Violations.Count.ShouldBe(1);
        result.Violations[0].ShouldBe("empty_body");
    }

    [Fact]
    public async Task CheckEvidenceFloor_WhitespaceOnlyBody_TrimsToEmpty_EmitsEmptyBodyViolation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 2, body: "   \n  \t  \r\n  ");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.PassesFloor.ShouldBeFalse();
        result.BodyLength.ShouldBe(0);
        result.Violations.ShouldContain("empty_body");
    }

    [Fact]
    public async Task CheckEvidenceFloor_BodyLength_ReportedAfterTrim()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        // Body has 4 leading and 4 trailing whitespace chars; substantive
        // length is 5 ("hello"). Floor must report the trimmed length.
        StubPrViewFloor(runner, prNumber: 42, commitCount: 1, body: "    hello    ");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.PassesFloor.ShouldBeTrue();
        result.BodyLength.ShouldBe(5);
    }

    [Fact]
    public async Task CheckEvidenceFloor_BothViolations_ListedInDeclarationOrder()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 0, body: "  ");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.PassesFloor.ShouldBeFalse();
        result.Violations.Count.ShouldBe(2);
        // Declaration order: no_commits FIRST, empty_body SECOND.
        // Workflow templates render in this order so operators see the
        // most "agent crashed" signal first.
        result.Violations[0].ShouldBe("no_commits");
        result.Violations[1].ShouldBe("empty_body");
    }

    // ─── Transport failures ──────────────────────────────────────────────

    [Fact]
    public async Task CheckEvidenceFloor_PrNotFound_EmitsPrNotFoundEnvelope()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFailure(runner, prNumber: 9999, exitCode: 1,
            stderr: "GraphQL: Could not resolve to a PullRequest with the number of 9999 (repository.pullRequest)");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 9999));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.PassesFloor.ShouldBeFalse();
        result.ErrorCode.ShouldBe("pr_not_found");
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        result.Violations.ShouldBeEmpty();
        result.CommitCount.ShouldBe(0);
        result.BodyLength.ShouldBe(0);
    }

    [Fact]
    public async Task CheckEvidenceFloor_PrNotFound_Http404_AlsoClassifiedAsPrNotFound()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFailure(runner, prNumber: 9999, exitCode: 1,
            stderr: "HTTP 404: Not Found (https://api.github.com/repos/owner/repo/pulls/9999)");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 9999));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task CheckEvidenceFloor_GhFailsForOtherReason_EmitsGhFailedEnvelope()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFailure(runner, prNumber: 42, exitCode: 1,
            stderr: "could not connect to github.com: dns error");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.PassesFloor.ShouldBeFalse();
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("dns error");
    }

    [Fact]
    public async Task CheckEvidenceFloor_GhReturnsMalformedJson_EmitsGhFailedEnvelope()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        runner.WhenStartsWith("gh", ["pr", "view", "42"],
            new ProcessResult(0, "this is not json{{{", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("malformed");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task CheckEvidenceFloor_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFloor(runner, prNumber: 42, commitCount: 1, body: "snake case me");

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        output.ShouldContain("\"success\"", Case.Sensitive);
        output.ShouldContain("\"pr_number\"", Case.Sensitive);
        output.ShouldContain("\"commit_count\"", Case.Sensitive);
        output.ShouldContain("\"body_length\"", Case.Sensitive);
        output.ShouldContain("\"passes_floor\"", Case.Sensitive);
        output.ShouldContain("\"violations\"", Case.Sensitive);
        // Snake-case discipline: never leak PascalCase names.
        output.ShouldNotContain("\"PrNumber\"", Case.Sensitive);
        output.ShouldNotContain("\"CommitCount\"", Case.Sensitive);
        output.ShouldNotContain("\"PassesFloor\"", Case.Sensitive);
    }

    [Fact]
    public async Task CheckEvidenceFloor_JsonContract_OnError_IncludesErrorCodeAndMessage()
    {
        var (cmd, runner) = CreateCommand();
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrViewFailure(runner, prNumber: 42, exitCode: 1,
            stderr: "could not resolve to a PullRequest");

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckEvidenceFloor(prNumber: 42));

        output.ShouldContain("\"error_code\"", Case.Sensitive);
        output.ShouldContain("\"error_message\"", Case.Sensitive);
        output.ShouldContain("\"pr_not_found\"", Case.Sensitive);
    }
}
