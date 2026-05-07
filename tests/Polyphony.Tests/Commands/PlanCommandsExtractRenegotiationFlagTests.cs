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
/// Tests for <c>polyphony plan extract-renegotiation-flag</c>. The verb
/// is a thin adapter over
/// <see cref="Polyphony.Manifest.RenegotiationFlagExtractor"/>; full
/// pure-extractor coverage lives in <c>RenegotiationFlagExtractorTests</c>.
/// Here we focus on the verb's I/O surface: gh stubbing, error envelopes,
/// the routing-style "always exit 0" contract, and the JSON shape.
/// </summary>
public sealed class PlanCommandsExtractRenegotiationFlagTests : CommandTestBase
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
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner)), runner);
    }

    private static PlanExtractRenegotiationFlagResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanExtractRenegotiationFlagResult)!;

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
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubPrViewMissing(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(1, "", "no pull requests found"));

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task NegativePrNumber_EmitsConfigErrorEnvelope_AlwaysExitsZero()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: -1, repo: Repo));
        exit.ShouldBe(ExitCodes.Success);
        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.PrNumber.ShouldBe(-1);
        r.ErrorCode.ShouldBe("config_error");
        r.ErrorMessage.ShouldNotBeNull();
        r.FlagPresent.ShouldBeFalse();
    }

    [Fact]
    public async Task ZeroPrNumber_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: 0, repo: Repo));
        Parse(output).ErrorCode.ShouldBe("config_error");
    }

    [Fact]
    public async Task RepoUnresolvable_EmitsRepoNotResolvedError()
    {
        var (cmd, runner) = CreateCommand();
        // No --repo override + git remote stub fails → slug resolves empty.
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: no such remote 'origin'"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber));
        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("repo_not_resolved");
    }

    // ─── Flag absent ─────────────────────────────────────────────────────

    [Fact]
    public async Task PrBodyHasNoFence_FlagAbsent_WellFormed()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "Just a plain plan PR body. Nothing to declare.");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeTrue();
        r.PrNumber.ShouldBe(PrNumber);
        r.FlagPresent.ShouldBeFalse();
        r.RenegotiationRequest.ShouldBeNull();
        r.FencedBlockWellFormed.ShouldBeTrue();
        r.ErrorCode.ShouldBeNull();
        r.ErrorMessage.ShouldBeNull();
        // Null fields must be omitted from the snake_case JSON.
        output.ShouldNotContain("\"renegotiation_request\":null");
        output.ShouldNotContain("\"error_code\":null");
    }

    [Fact]
    public async Task EmptyPrBody_FlagAbsent()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, "");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.FlagPresent.ShouldBeFalse();
        r.FencedBlockWellFormed.ShouldBeTrue();
    }

    // ─── Flag present, well-formed ──────────────────────────────────────

    [Fact]
    public async Task PrBodyHasFence_FlagPresent_ReasonExtracted()
    {
        const string reason = "Scope assumed feature/auth was already in main, but it is still in flight.";
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"intro\n{OpenTag}\n{reason}\n{CloseTag}\noutro");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeTrue();
        r.FlagPresent.ShouldBeTrue();
        r.RenegotiationRequest.ShouldBe(reason);
        r.FencedBlockWellFormed.ShouldBeTrue();
        r.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task MultipleBlocks_ConcatenatedWithBlankLineSeparator()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber,
            $"{OpenTag}\nfirst reason\n{CloseTag}\n\nmid\n\n{OpenTag}\nsecond reason\n{CloseTag}");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.FlagPresent.ShouldBeTrue();
        r.RenegotiationRequest.ShouldBe("first reason\n\nsecond reason");
    }

    [Fact]
    public async Task WhitespaceOnlyBlock_FlagPresent_ReasonEmptyAfterTrim()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"{OpenTag}\n   \n\t\n{CloseTag}");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.FlagPresent.ShouldBeTrue();
        r.FencedBlockWellFormed.ShouldBeTrue();
        // Flag is still considered present (the planner declared intent
        // even with empty content); reason is the empty string after trim.
        r.RenegotiationRequest.ShouldBe(string.Empty);
    }

    // ─── Flag malformed ──────────────────────────────────────────────────

    [Fact]
    public async Task OpeningTagWithoutClosing_Malformed_FlagAbsent()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"intro {OpenTag}\nreason without a closing tag\noutro");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeTrue(); // routing-style: success=true even when malformed
        r.FlagPresent.ShouldBeFalse();
        r.RenegotiationRequest.ShouldBeNull();
        r.FencedBlockWellFormed.ShouldBeFalse();
        r.ErrorCode.ShouldBe("malformed_renegotiation_block");
        r.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SecondBlockOpenWithoutClose_StillMalformed()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber,
            $"{OpenTag}\nclosed reason\n{CloseTag}\nthen later: {OpenTag}\nopen never closes");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.FencedBlockWellFormed.ShouldBeFalse();
        r.ErrorCode.ShouldBe("malformed_renegotiation_block");
    }

    // ─── PR not found ────────────────────────────────────────────────────

    [Fact]
    public async Task PrNotFound_EmitsPrNotFoundError()
    {
        var (cmd, runner) = CreateCommand();
        StubPrViewMissing(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));
        var r = Parse(output);
        r.Success.ShouldBeFalse();
        r.ErrorCode.ShouldBe("pr_not_found");
        r.ErrorMessage.ShouldNotBeNullOrEmpty();
        r.FlagPresent.ShouldBeFalse();
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task JsonContract_SnakeCaseKeys_NoPascalLeakage()
    {
        var (cmd, runner) = CreateCommand();
        StubPrView(runner, PrNumber, $"{OpenTag}\nx\n{CloseTag}");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ExtractRenegotiationFlag(prNumber: PrNumber, repo: Repo));

        output.ShouldContain("\"success\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"flag_present\"");
        output.ShouldContain("\"renegotiation_request\"");
        output.ShouldContain("\"fenced_block_well_formed\"");
        output.ShouldNotContain("PrNumber");
        output.ShouldNotContain("FlagPresent");
        output.ShouldNotContain("FencedBlockWellFormed");
    }
}
