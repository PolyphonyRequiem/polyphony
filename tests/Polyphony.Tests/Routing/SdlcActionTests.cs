using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for <see cref="SdlcAction"/> ensuring constant values match the documented
/// action scheme used by conductor scripts.
/// </summary>
public sealed class SdlcActionTests
{
    [Theory]
    [InlineData(nameof(SdlcAction.Plan), "plan")]
    [InlineData(nameof(SdlcAction.Seed), "seed")]
    [InlineData(nameof(SdlcAction.Implement), "implement")]
    [InlineData(nameof(SdlcAction.Monitor), "monitor")]
    [InlineData(nameof(SdlcAction.Close), "close")]
    [InlineData(nameof(SdlcAction.None), "none")]
    public void Action_MatchesDocumentedValue(string name, string expected)
    {
        var actual = name switch
        {
            nameof(SdlcAction.Plan) => SdlcAction.Plan,
            nameof(SdlcAction.Seed) => SdlcAction.Seed,
            nameof(SdlcAction.Implement) => SdlcAction.Implement,
            nameof(SdlcAction.Monitor) => SdlcAction.Monitor,
            nameof(SdlcAction.Close) => SdlcAction.Close,
            nameof(SdlcAction.None) => SdlcAction.None,
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        actual.ShouldBe(expected, $"SdlcAction.{name} should be \"{expected}\"");
    }

    [Fact]
    public void AllActions_AreDistinct()
    {
        var actions = new[]
        {
            SdlcAction.Plan,
            SdlcAction.Seed,
            SdlcAction.Implement,
            SdlcAction.Monitor,
            SdlcAction.Close,
            SdlcAction.None
        };

        actions.ShouldBeUnique();
    }

    [Fact]
    public void AllActions_UseLowercaseSnakeCase()
    {
        var actions = new[]
        {
            SdlcAction.Plan,
            SdlcAction.Seed,
            SdlcAction.Implement,
            SdlcAction.Monitor,
            SdlcAction.Close,
            SdlcAction.None
        };

        foreach (var action in actions)
        {
            action.ShouldBe(action.ToLowerInvariant(), $"Action \"{action}\" should be lowercase");
            action.ShouldNotContain(" ");
        }
    }
}
