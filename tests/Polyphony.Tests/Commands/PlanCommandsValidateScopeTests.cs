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
/// Tests for <c>polyphony plan validate-scope</c>. Covers the four-cell
/// scope × renegotiation-flag matrix, error envelopes (PR not found, repo
/// unresolved, malformed input), single + multi-glob coverage, and the
/// routing-style "always exit 0" contract.
/// </summary>
public sealed class PlanCommandsValidateScopeTests : CommandTestBase
{
    private const int PrNumber = 1234;
    private const string Repo = "acme/widgets";
    private const string OpenTag = "<!-- polyphony:requests-parent-change -->";
    private const string CloseTag = "<!-- /polyphony:requests-parent-change -->";

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new FakePostconditionVerifier()), runner);
    }

    private static PlanValidateScopeResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanValidateScopeResult)!;

    private static bool JsonArgContains(IReadOnlyList<string> args, string needle)
    {
        // The `gh pr view ... --json <value>` form passes a single comma-separated
        // string for <value>. We need to inspect *that string's contents* rather
        // than rely on token-equality with `args.Contains(...)`.
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--json" && args[i + 1].Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool JsonArgEquals(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--json" && args[i + 1] == value)
                return true;
        }
        return false;
    }

    private static void StubPrView(FakeProcessRunner runner, int prNumber, string body)
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "OPEN",
              "reviewDecision": "APPROVED",
              "mergeable": "MERGEABLE",
              "headRefName": "plan/100-200",
              "headRefOid": "deadbeef",
              "baseRefName": "plan/100",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        // gh pr view {n} --repo … --json number,state,…,body
        runner.When(
            (exe, args) => exe == "gh"
                && args.Count >= 4
                && args[0] == "pr" && args[1] == "view" && args[2] == prNumber.ToString()
                && JsonArgContains(args, "body"),
            new ProcessResult(0, json, ""));
    }

    private static void StubPrFiles(FakeProcessRunner runner, int prNumber, params string[] paths)
    {
        var entries = string.Join(",",
            paths.Select(p => $$"""{"path":"{{p}}","additions":1,"deletions":0}"""));
        var json = $$"""{ "files": [{{entries}}] }""";
        // gh pr view {n} --repo … --json files
        runner.When(
            (exe, args) => exe == "gh"
                && args.Count >= 4
                && args[0] == "pr" && args[1] == "view" && args[2] == prNumber.ToString()
                && JsonArgEquals(args, "files"),
            new ProcessResult(0, json, ""));
    }

    private static void StubPrViewMissing(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(1, "", "no pull requests found"));

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task NegativePrNumber_EmitsConfigError_AlwaysExitsZero()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: -5, childScope: "plans/**", repo: Repo));
        exit.ShouldBe(ExitCodes.Success);
        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.Verdict.ShouldBe("block");
        r.ErrorCode.ShouldBe("config_error");
    }

    [Fact]
    public async Task RepoUnresolvable_EmitsRepoNotResolvedError()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: no such remote 'origin'"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/**"));
        var r = Parse(output);
        r.ErrorCode.ShouldBe("repo_not_resolved");
        r.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task PrNotFound_EmitsPrNotFoundError()
    {
        var (cmd, runner) = CreateCommand();
        StubPrViewMissing(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/**", repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("pr_not_found");
    }

    // ─── Matrix cell 1: parent-touched + no flag → block ─────────────────

    [Fact]
    public async Task OutOfScopeFiles_NoFlag_BlocksWithScopeViolation()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body, no fence.");
        StubPrFiles(runner, PrNumber,
            "plans/1100/1101.md",
            "plans/1100/parent-amendment.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/1100/1101.md", repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeTrue();
        r.Verdict.ShouldBe("block");
        r.ErrorCode.ShouldBe("scope_violation_no_flag");
        r.FlagPresent.ShouldBeFalse();
        r.FilesTouched.ShouldBe(new[] { "plans/1100/1101.md", "plans/1100/parent-amendment.md" });
        r.FilesInScope.ShouldBe(new[] { "plans/1100/1101.md" });
        r.FilesOutOfScope.ShouldBe(new[] { "plans/1100/parent-amendment.md" });
    }

    // ─── Matrix cell 2: parent-touched + flag → allow ────────────────────

    [Fact]
    public async Task OutOfScopeFiles_WithFlag_Allows()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"{OpenTag}\nReplan from issue 1100.\n{CloseTag}");
        StubPrFiles(runner, PrNumber,
            "plans/1100/1101.md",
            "plans/1100/parent-amendment.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/1100/1101.md", repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("allow");
        r.ErrorCode.ShouldBeNull();
        r.FlagPresent.ShouldBeTrue();
        r.Warnings.ShouldBeEmpty();
        r.FilesOutOfScope.ShouldNotBeEmpty();
    }

    // ─── Matrix cell 3: !parent-touched + flag → allow + warn ────────────

    [Fact]
    public async Task NoOutOfScopeFiles_WithFlag_AllowsWithWarning()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"{OpenTag}\nConceptual renegotiation only.\n{CloseTag}");
        StubPrFiles(runner, PrNumber, "plans/1100/1101.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/1100/1101.md", repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("allow");
        r.FlagPresent.ShouldBeTrue();
        r.Warnings.ShouldContain("flag_without_parent_files");
        r.ErrorCode.ShouldBeNull();
    }

    // ─── Matrix cell 4: !parent-touched + !flag → allow ─────────────────

    [Fact]
    public async Task AllInScope_NoFlag_Allows_NoWarnings()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber, "plans/1100/1101.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/1100/1101.md", repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("allow");
        r.Warnings.ShouldBeEmpty();
        r.FlagPresent.ShouldBeFalse();
        r.FilesOutOfScope.ShouldBeEmpty();
    }

    // ─── Glob coverage ───────────────────────────────────────────────────

    [Fact]
    public async Task NoGlobsSupplied_EveryFileOutOfScope()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber, "plans/1100/1101.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "", repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("block");
        r.ErrorCode.ShouldBe("scope_violation_no_flag");
        r.FilesInScope.ShouldBeEmpty();
        r.FilesOutOfScope.ShouldBe(new[] { "plans/1100/1101.md" });
    }

    [Fact]
    public async Task MultipleGlobs_ClassifyEachPathIndependently()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber,
            "plans/1100/1101.md",
            "plans/1100/notes/r1.md",
            "src/code.cs");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(
                prNumber: PrNumber,
                childScope: "plans/1100/1101.md, plans/1100/notes/**",
                repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("block");
        r.FilesInScope.ShouldBe(new[] { "plans/1100/1101.md", "plans/1100/notes/r1.md" });
        r.FilesOutOfScope.ShouldBe(new[] { "src/code.cs" });
    }

    [Fact]
    public async Task DoubleStarGlob_MatchesAcrossSegments()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber,
            "plans/a.md",
            "plans/sub/b.md",
            "plans/sub/deep/c.md",
            "src/code.cs");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/**", repo: Repo));
        var r = Parse(output);
        r.FilesInScope.ShouldBe(new[] { "plans/a.md", "plans/sub/b.md", "plans/sub/deep/c.md" });
        r.FilesOutOfScope.ShouldBe(new[] { "src/code.cs" });
    }

    [Fact]
    public async Task SingleStarGlob_DoesNotCrossSegments()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber,
            "plans/a.md",
            "plans/sub/b.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/*.md", repo: Repo));
        var r = Parse(output);
        r.FilesInScope.ShouldBe(new[] { "plans/a.md" });
        r.FilesOutOfScope.ShouldBe(new[] { "plans/sub/b.md" });
    }

    [Fact]
    public async Task GlobWithNoMatches_OutOfScope()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber, "plans/1100/1101.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "docs/**", repo: Repo));
        var r = Parse(output);
        r.Verdict.ShouldBe("block");
        r.FilesInScope.ShouldBeEmpty();
        r.FilesOutOfScope.ShouldBe(new[] { "plans/1100/1101.md" });
    }

    // ─── ParseChildScope helper ──────────────────────────────────────────

    [Fact]
    public void ParseChildScope_Empty_ReturnsEmpty()
    {
        PlanCommands.ParseChildScope("").ShouldBeEmpty();
        PlanCommands.ParseChildScope("   ").ShouldBeEmpty();
    }

    [Fact]
    public void ParseChildScope_TrimsAndDedupes()
    {
        PlanCommands.ParseChildScope(" plans/**, plans/** , src/**")
            .ShouldBe(new[] { "plans/**", "src/**" });
    }

    // ─── JSON contract ──────────────────────────────────────────────────

    [Fact]
    public async Task JsonContract_SnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Plain PR body.");
        StubPrFiles(runner, PrNumber, "plans/1100/1101.md");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidateScope(prNumber: PrNumber, childScope: "plans/1100/1101.md", repo: Repo));
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"files_touched\"");
        output.ShouldContain("\"files_in_scope\"");
        output.ShouldContain("\"files_out_of_scope\"");
        output.ShouldContain("\"flag_present\"");
        output.ShouldContain("\"verdict\"");
        output.ShouldNotContain("PrNumber");
        output.ShouldNotContain("FilesTouched");
    }
}
