using Polyphony.Commands;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

/// <summary>
/// Unit tests for <see cref="ResearchCommands.ExtractCitation"/> and
/// <see cref="ResearchCommands.PrependCitationFrontMatter"/> — the
/// citation enrichment helpers used by the promotion writer.
/// </summary>
public sealed class CitationEnrichmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    // ── ExtractCitation ───────────────────────────────────────────────────

    [Fact]
    public void ExtractCitation_NoFrontMatter_ReturnsUnknownUrl()
    {
        var citation = ResearchCommands.ExtractCitation("# Plain content", Now);

        citation.SourceUrl.ShouldBe("unknown");
        citation.Freshness.ShouldBe("fresh");
    }

    [Fact]
    public void ExtractCitation_WithSourceUrl_ExtractsIt()
    {
        var content = "---\nsource_url: \"https://example.com\"\n---\nBody";
        var citation = ResearchCommands.ExtractCitation(content, Now);

        citation.SourceUrl.ShouldBe("https://example.com");
    }

    [Fact]
    public void ExtractCitation_WithCaptureDate_ComputesFreshness()
    {
        var staleDate = Now.AddDays(-10).ToString("o");
        var content = $"---\ncapture_date: \"{staleDate}\"\n---\nBody";
        var citation = ResearchCommands.ExtractCitation(content, Now);

        citation.Freshness.ShouldBe("stale");
    }

    [Fact]
    public void ExtractCitation_RecentCaptureDate_ReturnsRecent()
    {
        var recentDate = Now.AddDays(-3).ToString("o");
        var content = $"---\ncapture_date: \"{recentDate}\"\n---\nBody";
        var citation = ResearchCommands.ExtractCitation(content, Now);

        citation.Freshness.ShouldBe("recent");
    }

    [Fact]
    public void ExtractCitation_MalformedFrontMatter_ReturnsDefaults()
    {
        // Front matter without closing fence
        var content = "---\nsource_url: \"https://example.com\"\nno closing fence";
        var citation = ResearchCommands.ExtractCitation(content, Now);

        citation.SourceUrl.ShouldBe("unknown");
    }

    // ── PrependCitationFrontMatter ────────────────────────────────────────

    [Fact]
    public void PrependFrontMatter_PlainContent_AddsYamlFence()
    {
        var citation = new CitationMetadata
        {
            SourceUrl = "https://example.com",
            CaptureDate = "2026-05-11T12:00:00+00:00",
            Freshness = "fresh",
        };

        var result = ResearchCommands.PrependCitationFrontMatter("# Title\nBody", citation);

        result.ShouldStartWith("---\n");
        result.ShouldContain("source_url: \"https://example.com\"");
        result.ShouldContain("capture_date: \"2026-05-11T12:00:00+00:00\"");
        result.ShouldContain("freshness: \"fresh\"");
        result.ShouldContain("\n---\n");
        result.ShouldContain("# Title");
    }

    [Fact]
    public void PrependFrontMatter_ExistingFrontMatter_ReplacesIt()
    {
        var content = "---\nold_key: old_value\n---\n# Title\nBody";
        var citation = new CitationMetadata
        {
            SourceUrl = "https://new.example.com",
            CaptureDate = "2026-05-11T12:00:00+00:00",
            Freshness = "fresh",
        };

        var result = ResearchCommands.PrependCitationFrontMatter(content, citation);

        result.ShouldNotContain("old_key");
        result.ShouldContain("source_url: \"https://new.example.com\"");
        result.ShouldContain("# Title");
    }

    [Fact]
    public void PrependFrontMatter_PreservesBodyContent()
    {
        var citation = new CitationMetadata
        {
            SourceUrl = "https://example.com",
            CaptureDate = "2026-05-11T12:00:00+00:00",
            Freshness = "fresh",
        };

        var result = ResearchCommands.PrependCitationFrontMatter(
            "Line 1\nLine 2\nLine 3", citation);

        result.ShouldContain("Line 1\nLine 2\nLine 3");
    }
}
