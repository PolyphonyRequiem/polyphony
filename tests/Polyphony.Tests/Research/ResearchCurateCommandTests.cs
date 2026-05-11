using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Models;
using Polyphony.Research;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

/// <summary>
/// Tests for <c>polyphony research curate</c> — the archivist verb that
/// validates and emits per-artifact curation decisions.
/// </summary>
public sealed class ResearchCurateCommandTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;

    public ResearchCurateCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-curate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public new void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        base.Dispose();
    }

    private static ResearchCommands CreateCommand() =>
        new(new InMemoryResearchStore());

    private static ResearchCurateResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResearchCurateResult)!;

    private static string MakeDecisionsJson(params ArchivistDecision[] decisions) =>
        JsonSerializer.Serialize(decisions.ToList(), PolyphonyJsonContext.Default.ListArchivistDecision);

    private static ArchivistDecision MakeDecision(string path, string decision) =>
        new()
        {
            ArtifactPath = path,
            Decision = decision,
            Rationale = $"Test rationale for {decision}",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "high",
                Codebase = "medium",
                TechnologyStacks = "high",
                Ecosystem = "low",
                Linkability = "medium",
            },
        };

    // ── Missing required args ─────────────────────────────────────────────
    [Fact]
    public void MissingArgs_EmitsRoutingError()
    {
        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() => cmd.Curate());

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("research curate");
        envelope.MissingArgs.ShouldContain("--apex-id");
        envelope.MissingArgs.ShouldContain("--scratch-dir");
        envelope.MissingArgs.ShouldContain("--decisions-json");
    }

    // ── Scratch directory missing ─────────────────────────────────────────
    [Fact]
    public void ScratchDirMissing_EmitsError()
    {
        var cmd = CreateCommand();
        var decisions = MakeDecisionsJson(MakeDecision("a.md", CurationDecision.Keep));
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(100, "/nonexistent/path", decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("scratch_dir_missing");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("/nonexistent/path");
    }

    // ── Invalid decisions JSON ────────────────────────────────────────────
    [Fact]
    public void InvalidDecisionsJson_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(100, _tempDir, "not-valid-json"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_decisions_json");
    }

    // ── Invalid decision value ────────────────────────────────────────────
    [Fact]
    public void InvalidDecisionValue_EmitsError()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "content");
        var decisions = MakeDecisionsJson(MakeDecision("a.md", "invalid_value"));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(100, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_decision_value");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("invalid_value");
    }

    // ── Missing decision for scratch file ─────────────────────────────────
    [Fact]
    public void MissingDecisionForFile_EmitsError()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "content a");
        File.WriteAllText(Path.Combine(_tempDir, "b.md"), "content b");
        // Only provide decision for a.md, missing b.md
        var decisions = MakeDecisionsJson(MakeDecision("a.md", CurationDecision.Keep));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(100, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_decisions");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("b.md");
    }

    // ── Successful keep decision ──────────────────────────────────────────
    [Fact]
    public void KeepDecision_EmitsValidResult()
    {
        File.WriteAllText(Path.Combine(_tempDir, "article.md"), "# Article");
        var decisions = MakeDecisionsJson(MakeDecision("article.md", CurationDecision.Keep));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(42, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();
        result.ApexId.ShouldBe(42);
        result.ArtifactCount.ShouldBe(1);
        result.Decisions.Count.ShouldBe(1);
        result.Decisions[0].Decision.ShouldBe("keep");
        result.Decisions[0].ArtifactPath.ShouldBe("article.md");
        result.Decisions[0].RelevanceSignals.ShouldNotBeNull();
        result.Decisions[0].RelevanceSignals.Domain.ShouldBe("high");
    }

    // ── Successful discard decision ───────────────────────────────────────
    [Fact]
    public void DiscardDecision_EmitsValidResult()
    {
        File.WriteAllText(Path.Combine(_tempDir, "noise.md"), "irrelevant");
        var decisions = MakeDecisionsJson(MakeDecision("noise.md", CurationDecision.Discard));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(42, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Decisions[0].Decision.ShouldBe("discard");
    }

    // ── Successful expand decision ────────────────────────────────────────
    [Fact]
    public void ExpandDecision_EmitsValidResult()
    {
        File.WriteAllText(Path.Combine(_tempDir, "promising.md"), "needs more");
        var decisions = MakeDecisionsJson(MakeDecision("promising.md", CurationDecision.Expand));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(42, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Decisions[0].Decision.ShouldBe("expand");
    }

    // ── Mixed decisions ───────────────────────────────────────────────────
    [Fact]
    public void MixedDecisions_AllBranchesPresent()
    {
        File.WriteAllText(Path.Combine(_tempDir, "keep.md"), "keep me");
        File.WriteAllText(Path.Combine(_tempDir, "discard.md"), "drop me");
        File.WriteAllText(Path.Combine(_tempDir, "expand.md"), "dig deeper");
        var decisions = MakeDecisionsJson(
            MakeDecision("keep.md", CurationDecision.Keep),
            MakeDecision("discard.md", CurationDecision.Discard),
            MakeDecision("expand.md", CurationDecision.Expand));

        var cmd = CreateCommand();
        var (exit, output) = CaptureConsole(() =>
            cmd.Curate(99, _tempDir, decisions));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ArtifactCount.ShouldBe(3);
        result.Decisions.Select(d => d.Decision).ShouldBe(
            ["keep", "discard", "expand"], ignoreOrder: false);
    }

    // ── JSON round-trip schema contract ───────────────────────────────────
    [Fact]
    public void DecisionSchema_RoundTripsSnakeCase()
    {
        var decision = MakeDecision("test.md", CurationDecision.Keep);
        var json = JsonSerializer.Serialize(decision, PolyphonyJsonContext.Default.ArchivistDecision);

        json.ShouldContain("\"artifact_path\"");
        json.ShouldContain("\"decision\"");
        json.ShouldContain("\"rationale\"");
        json.ShouldContain("\"relevance_signals\"");
        json.ShouldContain("\"technology_stacks\"");

        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ArchivistDecision);
        roundTripped.ShouldNotBeNull();
        roundTripped!.ArtifactPath.ShouldBe("test.md");
        roundTripped.Decision.ShouldBe("keep");
        roundTripped.RelevanceSignals.TechnologyStacks.ShouldBe("high");
    }
}
