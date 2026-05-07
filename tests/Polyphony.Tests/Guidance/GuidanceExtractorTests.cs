using Polyphony.Guidance;
using Polyphony.Sdlc;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Aggregates;
using Xunit;

namespace Polyphony.Tests.Guidance;

/// <summary>
/// Unit tests for <see cref="GuidanceExtractor.Extract"/>. Covers both source
/// branches (description block + ADO field), the multi-block concatenation
/// contract, whitespace handling, and the silent-skip behaviour for opening
/// tags with no closing tag.
/// </summary>
public sealed class GuidanceExtractorTests
{
    private const string OpenTag = "<!-- polyphony:guidance -->";
    private const string CloseTag = "<!-- /polyphony:guidance -->";

    private static GuidanceConfig DescriptionBlockConfig() =>
        new(GuidanceSource.DescriptionBlock, AdoFieldName: null);

    private static GuidanceConfig AdoFieldConfig(string fieldName) =>
        new(GuidanceSource.AdoField, AdoFieldName: fieldName);

    private static WorkItem WithDescription(string? description) =>
        new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithTitle("T")
            .WithField("System.Description", description)
            .Build();

    [Fact]
    public void Extract_NoBlocks_ReturnsNull()
    {
        var item = WithDescription("Just a regular description, no fenced block here.");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_NoDescriptionField_ReturnsNull()
    {
        // No System.Description set at all.
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithTitle("T").Build();
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_SingleBlock_ReturnsContent()
    {
        var item = WithDescription($"prefix\n{OpenTag}\nUse Foo, not Bar.\n{CloseTag}\nsuffix");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBe("Use Foo, not Bar.");
    }

    [Fact]
    public void Extract_MultipleBlocks_ConcatenatesWithSeparator()
    {
        var description =
            $"intro\n{OpenTag}\nFirst.\n{CloseTag}\nbetween\n{OpenTag}\nSecond.\n{CloseTag}\noutro";
        var item = WithDescription(description);

        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());

        result.ShouldBe("First.\n\n---\n\nSecond.");
    }

    [Fact]
    public void Extract_BlockWithLeadingTrailingWhitespace_TrimsOuter()
    {
        var item = WithDescription($"{OpenTag}\n\n   Padded body.   \n\n{CloseTag}");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBe("Padded body.");
    }

    [Fact]
    public void Extract_BlockWithInternalWhitespace_PreservesInner()
    {
        var inner = "Line one.\n\n  Line two with leading spaces.\nLine\tthree with tab.";
        var item = WithDescription($"{OpenTag}\n{inner}\n{CloseTag}");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBe(inner);
    }

    [Fact]
    public void Extract_OpeningTagOnly_ReturnsNullSilently()
    {
        // Opening with no matching closing — best-effort silent skip.
        var item = WithDescription($"some text\n{OpenTag}\nUnterminated guidance here.");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_OpeningTagOnly_WithLaterCompleteBlock_GreedilyConsumesToFirstClose()
    {
        // Documenting current behaviour: identical opening tags mean the regex
        // pairs the FIRST opener with the FIRST closer, so an "unterminated"
        // opener earlier in the description swallows everything up to the next
        // close tag. We don't try to be cleverer here — a malformed block
        // anywhere in the description should be flagged at authoring time;
        // see Phase 6 design sketch open question #5 for the deferred
        // work-item-content linter.
        var item = WithDescription(
            $"intro\n{OpenTag}\nincomplete... {OpenTag}\nReal guidance.\n{CloseTag}\noutro");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldNotBeNull();
        result.ShouldContain("Real guidance.");
    }

    [Fact]
    public void Extract_CaseSensitiveTag_DoesNotMatchUpperCase()
    {
        var item = WithDescription("<!-- POLYPHONY:GUIDANCE -->\nNope.\n<!-- /POLYPHONY:GUIDANCE -->");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_EmptyBlock_ReturnsEmptyString()
    {
        // Block exists but contains only whitespace; outer-trim leaves empty —
        // the block IS present so we surface the empty string (NOT null).
        // Distinguishes "no block" from "empty block".
        var item = WithDescription($"{OpenTag}\n   \n{CloseTag}");
        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBe("");
    }

    // ──────────────────────── ADO field source ─────────────────────────────

    [Fact]
    public void Extract_AdoFieldSource_PullsFromCustomField()
    {
        var item = new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithTitle("T")
            .WithField("Custom.Guidance", "Use OpenTelemetry for traces.")
            .Build();

        var result = GuidanceExtractor.Extract(item, AdoFieldConfig("Custom.Guidance"));
        result.ShouldBe("Use OpenTelemetry for traces.");
    }

    [Fact]
    public void Extract_AdoFieldSource_FieldMissing_ReturnsNull()
    {
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithTitle("T").Build();
        var result = GuidanceExtractor.Extract(item, AdoFieldConfig("Custom.Guidance"));
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_AdoFieldSource_FieldEmpty_ReturnsNull()
    {
        var item = new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithTitle("T")
            .WithField("Custom.Guidance", "   ")
            .Build();

        var result = GuidanceExtractor.Extract(item, AdoFieldConfig("Custom.Guidance"));
        result.ShouldBeNull();
    }

    [Fact]
    public void Extract_AdoFieldSource_NullFieldName_Throws()
    {
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithTitle("T").Build();
        Should.Throw<ArgumentException>(() =>
            GuidanceExtractor.Extract(item, new GuidanceConfig(GuidanceSource.AdoField, AdoFieldName: null)));
    }

    [Fact]
    public void Extract_DescriptionBlockSource_IgnoresAdoField()
    {
        // Source is description_block — even if a custom field exists with content,
        // it must NOT be read.
        var item = new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithTitle("T")
            .WithField("System.Description", $"prefix\n{OpenTag}\nFrom description.\n{CloseTag}")
            .WithField("Custom.Guidance", "From the ADO field — should be ignored.")
            .Build();

        var result = GuidanceExtractor.Extract(item, DescriptionBlockConfig());
        result.ShouldBe("From description.");
    }

    [Fact]
    public void Extract_AdoFieldSource_IgnoresDescriptionBlock()
    {
        // Symmetric: source is ado_field — description blocks must not be read.
        var item = new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithTitle("T")
            .WithField("System.Description", $"{OpenTag}\nShould not be returned.\n{CloseTag}")
            .WithField("Custom.Guidance", "From the ADO field.")
            .Build();

        var result = GuidanceExtractor.Extract(item, AdoFieldConfig("Custom.Guidance"));
        result.ShouldBe("From the ADO field.");
    }

    [Fact]
    public void Extract_NullWorkItem_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            GuidanceExtractor.Extract(null!, DescriptionBlockConfig()));

    [Fact]
    public void Extract_NullConfig_Throws()
    {
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithTitle("T").Build();
        Should.Throw<ArgumentNullException>(() => GuidanceExtractor.Extract(item, null!));
    }

    [Fact]
    public void Extract_UnknownSource_Throws()
    {
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithTitle("T").Build();
        Should.Throw<ArgumentException>(() =>
            GuidanceExtractor.Extract(item, new GuidanceConfig("not_a_real_source", AdoFieldName: null)));
    }
}

/// <summary>
/// Small structural tests for the <see cref="GuidanceSource"/> string-constant type.
/// Mirrors the pattern in <c>SdlcConstantsTests</c>.
/// </summary>
public sealed class GuidanceSourceConstantTests
{
    [Theory]
    [InlineData("description_block", true)]
    [InlineData("ado_field", true)]
    [InlineData("Description_Block", false)]
    [InlineData("DESCRIPTION_BLOCK", false)]
    [InlineData("custom_field", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid(string? value, bool expected) =>
        GuidanceSource.IsValid(value).ShouldBe(expected);
}
