using System.Text.Json;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

/// <summary>
/// Schema contract tests for the research models: archivist decisions,
/// citation metadata, destinations, and write results. Verifies that the
/// JSON wire format matches the documented schema so downstream consumers
/// (promotion writer, loop-back, harness) can rely on the shape.
/// </summary>
public sealed class ResearchSchemaTests
{
    [Fact]
    public void Decision_SerializesToSnakeCase()
    {
        var decision = new ArchivistDecision
        {
            ArtifactPath = "research/article.md",
            Decision = CurationDecision.Keep,
            Rationale = "Directly relevant to domain architecture.",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "high",
                Codebase = "medium",
                TechnologyStacks = "high",
                Ecosystem = "low",
                Linkability = "medium",
            },
        };

        var json = JsonSerializer.Serialize(decision, PolyphonyJsonContext.Default.ArchivistDecision);

        json.ShouldContain("\"artifact_path\"");
        json.ShouldContain("\"decision\"");
        json.ShouldContain("\"rationale\"");
        json.ShouldContain("\"relevance_signals\"");
        json.ShouldContain("\"technology_stacks\"");
        json.ShouldContain("\"keep\"");
        json.ShouldContain("\"research/article.md\"");
    }

    [Fact]
    public void Decision_RoundTrips()
    {
        var original = new ArchivistDecision
        {
            ArtifactPath = "notes/api-design.md",
            Decision = CurationDecision.Expand,
            Rationale = "Promising but needs deeper analysis of versioning strategy.",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "high",
                Codebase = "low",
                TechnologyStacks = "medium",
                Ecosystem = "high",
                Linkability = "high",
            },
        };

        var json = JsonSerializer.Serialize(original, PolyphonyJsonContext.Default.ArchivistDecision);
        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ArchivistDecision);

        roundTripped.ShouldNotBeNull();
        roundTripped!.ArtifactPath.ShouldBe(original.ArtifactPath);
        roundTripped.Decision.ShouldBe(original.Decision);
        roundTripped.Rationale.ShouldBe(original.Rationale);
        roundTripped.RelevanceSignals.Domain.ShouldBe("high");
        roundTripped.RelevanceSignals.TechnologyStacks.ShouldBe("medium");
        roundTripped.RelevanceSignals.Linkability.ShouldBe("high");
    }

    [Fact]
    public void DecisionList_RoundTrips()
    {
        var decisions = new List<ArchivistDecision>
        {
            new()
            {
                ArtifactPath = "a.md",
                Decision = CurationDecision.Keep,
                Rationale = "Relevant",
                RelevanceSignals = MakeSignals(),
            },
            new()
            {
                ArtifactPath = "b.md",
                Decision = CurationDecision.Discard,
                Rationale = "Not relevant",
                RelevanceSignals = MakeSignals(),
            },
            new()
            {
                ArtifactPath = "c.md",
                Decision = CurationDecision.Expand,
                Rationale = "Needs work",
                RelevanceSignals = MakeSignals(),
            },
        };

        var json = JsonSerializer.Serialize(decisions, PolyphonyJsonContext.Default.ListArchivistDecision);
        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ListArchivistDecision);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Count.ShouldBe(3);
        roundTripped[0].Decision.ShouldBe("keep");
        roundTripped[1].Decision.ShouldBe("discard");
        roundTripped[2].Decision.ShouldBe("expand");
    }

    [Theory]
    [InlineData("keep", true)]
    [InlineData("discard", true)]
    [InlineData("expand", true)]
    [InlineData("KEEP", false)]
    [InlineData("invalid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CurationDecision_IsValid(string? value, bool expected) =>
        CurationDecision.IsValid(value).ShouldBe(expected);

    [Fact]
    public void CitationMetadata_SerializesToSnakeCase()
    {
        var citation = new CitationMetadata
        {
            SourceUrl = "https://example.com/article",
            CaptureDate = "2026-05-11T10:00:00+00:00",
            Freshness = "fresh",
        };

        var json = JsonSerializer.Serialize(citation, PolyphonyJsonContext.Default.CitationMetadata);

        json.ShouldContain("\"source_url\"");
        json.ShouldContain("\"capture_date\"");
        json.ShouldContain("\"freshness\"");
    }

    [Theory]
    [InlineData(0, "fresh")]     // 0 hours
    [InlineData(23, "fresh")]    // 23 hours
    [InlineData(25, "recent")]   // 25 hours = > 24h
    [InlineData(167, "recent")]  // 167 hours = < 7 days
    [InlineData(169, "stale")]   // 169 hours = > 7 days
    [InlineData(720, "stale")]   // 30 days
    public void ComputeFreshness_ReturnsCorrectBand(int hoursAgo, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var captureTime = now.AddHours(-hoursAgo);
        CitationMetadata.ComputeFreshness(captureTime, now).ShouldBe(expected);
    }

    [Fact]
    public void ResearchDestination_SerializesToSnakeCase()
    {
        var dest = new ResearchDestination
        {
            Platform = "azure_devops",
            RepoLocator = "org/project/repo",
            Branch = "main",
            RootPath = "articles",
        };

        var json = JsonSerializer.Serialize(dest, PolyphonyJsonContext.Default.ResearchDestination);
        json.ShouldContain("\"platform\"");
        json.ShouldContain("\"repo_locator\"");
        json.ShouldContain("\"branch\"");
        json.ShouldContain("\"root_path\"");

        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResearchDestination);
        roundTripped.ShouldNotBeNull();
        roundTripped!.Platform.ShouldBe("azure_devops");
        roundTripped.RootPath.ShouldBe("articles");
    }

    [Fact]
    public void ResearchPromoteResult_RoundTrips()
    {
        var result = new Polyphony.Models.ResearchPromoteResult
        {
            ApexId = 42,
            Promoted = ["a.md", "b.md"],
            ExpandRequested = ["c.md"],
            DiscardedCount = 1,
            PlatformCombo = "source:github+research:azure_devops",
        };

        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResearchPromoteResult);
        json.ShouldContain("\"apex_id\"");
        json.ShouldContain("\"promoted\"");
        json.ShouldContain("\"expand_requested\"");
        json.ShouldContain("\"discarded_count\"");
        json.ShouldContain("\"platform_combo\"");

        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResearchPromoteResult);
        roundTripped.ShouldNotBeNull();
        roundTripped!.ApexId.ShouldBe(42);
        roundTripped.Promoted.ShouldBe(["a.md", "b.md"]);
        roundTripped.ExpandRequested.ShouldBe(["c.md"]);
        roundTripped.DiscardedCount.ShouldBe(1);
    }

    private static RelevanceSignals MakeSignals() =>
        new()
        {
            Domain = "medium",
            Codebase = "medium",
            TechnologyStacks = "medium",
            Ecosystem = "medium",
            Linkability = "medium",
        };
}
