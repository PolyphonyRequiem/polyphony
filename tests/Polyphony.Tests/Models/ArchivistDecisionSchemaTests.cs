using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Models;

/// <summary>
/// Contract tests for the archivist decision schema. Pins the JSON shape
/// (property names, nesting, snake_case naming) so downstream consumers
/// (promotion writer, expand loop) can rely on a stable contract.
/// Tests cover a representative decision for each verdict branch.
/// </summary>
public sealed class ArchivistDecisionSchemaTests
{
    private static readonly RelevanceSignals SampleSignals = new()
    {
        Domain = "high",
        Codebase = "medium",
        TechnologyStacks = "low",
        Ecosystem = "none",
        Linkability = "high",
    };

    // =========================================================================
    // ArchivistVerdict constants
    // =========================================================================

    [Theory]
    [InlineData("keep", true)]
    [InlineData("discard", true)]
    [InlineData("expand", true)]
    [InlineData("Keep", false)]
    [InlineData("KEEP", false)]
    [InlineData("archive", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_ReturnsExpected(string? value, bool expected)
    {
        ArchivistVerdict.IsValid(value).ShouldBe(expected);
    }

    // =========================================================================
    // Per-verdict schema shape (keep / discard / expand)
    // =========================================================================

    [Fact]
    public void Keep_Decision_Serializes_WithCorrectShape()
    {
        var decision = new ArchivistDecision
        {
            Artifact = "notes/api-design.md",
            Decision = ArchivistVerdict.Keep,
            Rationale = "Comprehensive API design notes directly applicable to the target codebase.",
            RelevanceSignals = SampleSignals,
        };

        var json = Serialize(decision);

        json.ShouldContain("\"artifact\":\"notes/api-design.md\"");
        json.ShouldContain("\"decision\":\"keep\"");
        json.ShouldContain("\"rationale\":");
        json.ShouldContain("\"relevance_signals\":");
        AssertSignalKeys(json);
    }

    [Fact]
    public void Discard_Decision_Serializes_WithCorrectShape()
    {
        var decision = new ArchivistDecision
        {
            Artifact = "temp/draft.txt",
            Decision = ArchivistVerdict.Discard,
            Rationale = "Temporary scratch notes, no lasting value.",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "none",
                Codebase = "none",
                TechnologyStacks = "none",
                Ecosystem = "none",
                Linkability = "none",
            },
        };

        var json = Serialize(decision);

        json.ShouldContain("\"decision\":\"discard\"");
        json.ShouldContain("\"artifact\":\"temp/draft.txt\"");
    }

    [Fact]
    public void Expand_Decision_Serializes_WithCorrectShape()
    {
        var decision = new ArchivistDecision
        {
            Artifact = "research/auth-patterns.md",
            Decision = ArchivistVerdict.Expand,
            Rationale = "Auth patterns relevant but coverage is shallow; needs deeper investigation.",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "high",
                Codebase = "high",
                TechnologyStacks = "medium",
                Ecosystem = "medium",
                Linkability = "low",
            },
        };

        var json = Serialize(decision);

        json.ShouldContain("\"decision\":\"expand\"");
        json.ShouldContain("\"artifact\":\"research/auth-patterns.md\"");
    }

    // =========================================================================
    // RelevanceSignals schema
    // =========================================================================

    [Fact]
    public void RelevanceSignals_Serializes_AllFiveAxes_SnakeCase()
    {
        var json = JsonSerializer.Serialize(
            SampleSignals, PolyphonyJsonContext.Default.RelevanceSignals);

        json.ShouldContain("\"domain\":\"high\"");
        json.ShouldContain("\"codebase\":\"medium\"");
        json.ShouldContain("\"technology_stacks\":\"low\"");
        json.ShouldContain("\"ecosystem\":\"none\"");
        json.ShouldContain("\"linkability\":\"high\"");

        // Must NOT leak PascalCase property names. Only check multi-word
        // properties where PascalCase differs from snake_case.
        json.ShouldNotContain("TechnologyStacks");
    }

    // =========================================================================
    // ArchivistResult envelope
    // =========================================================================

    [Fact]
    public void ArchivistResult_Success_Serializes_WithDecisions()
    {
        var result = new ArchivistResult
        {
            Apex = 3071,
            ScratchPath = "research/scratch/3071",
            Decisions =
            [
                new ArchivistDecision
                {
                    Artifact = "a.md",
                    Decision = ArchivistVerdict.Keep,
                    Rationale = "Useful.",
                    RelevanceSignals = SampleSignals,
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.ArchivistResult);

        json.ShouldContain("\"apex\":3071");
        json.ShouldContain("\"scratch_path\":\"research/scratch/3071\"");
        json.ShouldContain("\"decisions\":");
        json.ShouldNotContain("\"error\"");
        json.ShouldNotContain("\"error_code\"");
    }

    [Fact]
    public void ArchivistResult_Error_OmitsDecisions_ShowsErrorFields()
    {
        var result = new ArchivistResult
        {
            Apex = 42,
            ScratchPath = "",
            Decisions = [],
            Error = "Scratch directory not found.",
            ErrorCode = "scratch_dir_not_found",
        };

        var json = JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.ArchivistResult);

        json.ShouldContain("\"error\":\"Scratch directory not found.\"");
        json.ShouldContain("\"error_code\":\"scratch_dir_not_found\"");
        json.ShouldContain("\"apex\":42");
    }

    [Fact]
    public void ArchivistResult_RoundTrip_PreservesAllFields()
    {
        var original = new ArchivistResult
        {
            Apex = 100,
            ScratchPath = "research/scratch/100",
            Decisions =
            [
                new ArchivistDecision
                {
                    Artifact = "file.md",
                    Decision = ArchivistVerdict.Expand,
                    Rationale = "Needs more depth.",
                    RelevanceSignals = new RelevanceSignals
                    {
                        Domain = "high",
                        Codebase = "low",
                        TechnologyStacks = "medium",
                        Ecosystem = "high",
                        Linkability = "medium",
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            original, PolyphonyJsonContext.Default.ArchivistResult);
        var deserialized = JsonSerializer.Deserialize(
            json, PolyphonyJsonContext.Default.ArchivistResult);

        deserialized.ShouldNotBeNull();
        deserialized.Apex.ShouldBe(100);
        deserialized.ScratchPath.ShouldBe("research/scratch/100");
        deserialized.Decisions.Count.ShouldBe(1);

        var d = deserialized.Decisions[0];
        d.Artifact.ShouldBe("file.md");
        d.Decision.ShouldBe(ArchivistVerdict.Expand);
        d.Rationale.ShouldBe("Needs more depth.");
        d.RelevanceSignals.Domain.ShouldBe("high");
        d.RelevanceSignals.Codebase.ShouldBe("low");
        d.RelevanceSignals.TechnologyStacks.ShouldBe("medium");
        d.RelevanceSignals.Ecosystem.ShouldBe("high");
        d.RelevanceSignals.Linkability.ShouldBe("medium");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string Serialize(ArchivistDecision decision) =>
        JsonSerializer.Serialize(decision, PolyphonyJsonContext.Default.ArchivistDecision);

    private static void AssertSignalKeys(string json)
    {
        json.ShouldContain("\"domain\":");
        json.ShouldContain("\"codebase\":");
        json.ShouldContain("\"technology_stacks\":");
        json.ShouldContain("\"ecosystem\":");
        json.ShouldContain("\"linkability\":");
    }
}
