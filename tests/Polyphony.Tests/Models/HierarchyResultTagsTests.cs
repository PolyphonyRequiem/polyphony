using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Models;

/// <summary>
/// Tests for the <see cref="HierarchyResult.Tags"/> property,
/// verifying default value, serialization, and deserialization behavior.
/// </summary>
public sealed class HierarchyResultTagsTests
{
    private static HierarchyResult CreateResult(string? tags = null) => new()
    {
        WorkItemId = 1,
        Title = "Test Item",
        Type = "Epic",
        Capabilities = ["plannable"],
        State = "Doing",
        Tags = tags,
    };

    [Fact]
    public void Tags_DefaultsToNull()
    {
        var result = new HierarchyResult
        {
            WorkItemId = 1,
            Title = "Test",
            Type = "Epic",
            Capabilities = [],
            State = "New",
        };

        result.Tags.ShouldBeNull();
    }

    [Fact]
    public void Tags_WhenNull_OmittedFromJson()
    {
        var result = CreateResult(tags: null);
        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult);

        json.ShouldNotContain("\"tags\"");
    }

    [Fact]
    public void Tags_WhenSet_IncludedInJsonAsSnakeCase()
    {
        var result = CreateResult(tags: "PG-1; twig");
        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult);

        json.ShouldContain("\"tags\":\"PG-1; twig\"");
    }

    [Fact]
    public void Tags_RoundTripsViaJson()
    {
        var original = CreateResult(tags: "PG-1; twig");
        var json = JsonSerializer.Serialize(original, PolyphonyJsonContext.Default.HierarchyResult);
        var deserialized = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.HierarchyResult);

        deserialized.ShouldNotBeNull();
        deserialized.Tags.ShouldBe("PG-1; twig");
    }

    [Fact]
    public void Tags_EmptyString_IncludedInJson()
    {
        var result = CreateResult(tags: "");
        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult);

        json.ShouldContain("\"tags\":\"\"");
    }

    [Fact]
    public void Tags_DeserializesFromJsonWithoutTags()
    {
        var json = """{"work_item_id":1,"title":"Test","type":"Epic","capabilities":[],"state":"New"}""";
        var result = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.HierarchyResult);

        result.ShouldNotBeNull();
        result.Tags.ShouldBeNull();
    }
}
