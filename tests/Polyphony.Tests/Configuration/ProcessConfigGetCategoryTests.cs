using Polyphony.Configuration;
using Shouldly;
using Twig.Domain.Enums;
using Xunit;

namespace Polyphony.Tests.Configuration;

/// <summary>
/// Coverage for <see cref="ProcessConfig.GetCategory"/> and
/// <see cref="ProcessConfig.ParseCategory"/>. The state→category mapping is
/// the per-config replacement for the legacy <c>StateCategoryResolver</c>
/// heuristic — see issue #281 and docs/decisions/states-in-process-config.md.
/// </summary>
public sealed class ProcessConfigGetCategoryTests
{
    [Fact]
    public void GetCategory_KnownTypeAndState_ReturnsDeclaredCategory()
    {
        var config = MakeConfig();

        config.GetCategory("Epic", "Doing").ShouldBe(StateCategory.InProgress);
        config.GetCategory("Epic", "Done").ShouldBe(StateCategory.Completed);
        config.GetCategory("Epic", "To Do").ShouldBe(StateCategory.Proposed);
    }

    [Fact]
    public void GetCategory_TypeNameCaseInsensitive_ReturnsDeclaredCategory()
    {
        var config = MakeConfig();

        config.GetCategory("epic", "Doing").ShouldBe(StateCategory.InProgress);
        config.GetCategory("EPIC", "Doing").ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void GetCategory_StateNameCaseInsensitive_ReturnsDeclaredCategory()
    {
        var config = MakeConfig();

        config.GetCategory("Epic", "doing").ShouldBe(StateCategory.InProgress);
        config.GetCategory("Epic", "DOING").ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void GetCategory_UnknownType_ReturnsUnknown()
    {
        var config = MakeConfig();

        config.GetCategory("NonExistent", "Doing").ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void GetCategory_UnknownState_ReturnsUnknown()
    {
        var config = MakeConfig();

        config.GetCategory("Epic", "NotDeclared").ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void GetCategory_NullState_ReturnsUnknown()
    {
        var config = MakeConfig();

        config.GetCategory("Epic", null).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void GetCategory_EmptyState_ReturnsUnknown()
    {
        var config = MakeConfig();

        config.GetCategory("Epic", "").ShouldBe(StateCategory.Unknown);
    }

    [Theory]
    [InlineData("proposed", StateCategory.Proposed)]
    [InlineData("in_progress", StateCategory.InProgress)]
    [InlineData("resolved", StateCategory.Resolved)]
    [InlineData("completed", StateCategory.Completed)]
    [InlineData("removed", StateCategory.Removed)]
    public void ParseCategory_CanonicalSnakeCase_Parses(string raw, StateCategory expected)
    {
        ProcessConfig.ParseCategory(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("in-progress")]
    [InlineData("IN_PROGRESS")]
    [InlineData(" in_progress ")]
    [InlineData("In Progress")]
    public void ParseCategory_NormalizesWhitespaceAndDelimiters(string raw)
    {
        ProcessConfig.ParseCategory(raw).ShouldBe(StateCategory.InProgress);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("doing")]
    [InlineData("inprogess")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ParseCategory_UnknownString_ReturnsUnknown(string? raw)
    {
        ProcessConfig.ParseCategory(raw).ShouldBe(StateCategory.Unknown);
    }

    private static ProcessConfig MakeConfig()
    {
        return new ProcessConfig
        {
            ProcessTemplate = "Basic",
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Facets = ["plannable"] },
            },
            States = new Dictionary<string, Dictionary<string, string>>
            {
                ["Epic"] = new Dictionary<string, string>
                {
                    ["To Do"] = "proposed",
                    ["Doing"] = "in_progress",
                    ["Done"] = "completed",
                },
            },
        };
    }
}
