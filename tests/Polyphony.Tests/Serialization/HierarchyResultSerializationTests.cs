using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Serialization;

/// <summary>
/// Verifies that <see cref="PolyphonyJsonContext"/> correctly serializes
/// the <see cref="HierarchyResult.Tags"/> property using snake_case naming
/// and omits it when null (WhenWritingNull).
/// </summary>
public sealed class HierarchyResultSerializationTests
{
    [Fact]
    public void Serialize_WithTags_IncludesTagsField()
    {
        var result = new HierarchyResult
        {
            WorkItemId = 1, Title = "Test", Type = "Epic",
            Facets = ["plannable"], State = "Doing",
            Tags = "PG-1; Sprint 5"
        };

        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult);

        json.ShouldContain("\"tags\":\"PG-1; Sprint 5\"");
    }

    [Fact]
    public void Serialize_WithoutTags_OmitsTagsField()
    {
        var result = new HierarchyResult
        {
            WorkItemId = 1, Title = "Test", Type = "Epic",
            Facets = ["plannable"], State = "Doing"
        };

        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult);

        json.ShouldNotContain("\"tags\"");
    }
}

