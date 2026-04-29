using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for <see cref="SdlcPhase"/> ensuring constant values match the documented
/// phase detection rules table used by conductor scripts.
/// </summary>
public sealed class SdlcPhaseTests
{
    [Theory]
    [InlineData(nameof(SdlcPhase.NeedsPlanning), "needs_planning")]
    [InlineData(nameof(SdlcPhase.NeedsSeeding), "needs_seeding")]
    [InlineData(nameof(SdlcPhase.ReadyForImplementation), "ready_for_implementation")]
    [InlineData(nameof(SdlcPhase.InProgress), "in_progress")]
    [InlineData(nameof(SdlcPhase.ReadyForCompletion), "ready_for_completion")]
    [InlineData(nameof(SdlcPhase.Done), "done")]
    [InlineData(nameof(SdlcPhase.Removed), "removed")]
    [InlineData(nameof(SdlcPhase.Unknown), "unknown")]
    public void Phase_MatchesDocumentedValue(string name, string expected)
    {
        var actual = name switch
        {
            nameof(SdlcPhase.NeedsPlanning) => SdlcPhase.NeedsPlanning,
            nameof(SdlcPhase.NeedsSeeding) => SdlcPhase.NeedsSeeding,
            nameof(SdlcPhase.ReadyForImplementation) => SdlcPhase.ReadyForImplementation,
            nameof(SdlcPhase.InProgress) => SdlcPhase.InProgress,
            nameof(SdlcPhase.ReadyForCompletion) => SdlcPhase.ReadyForCompletion,
            nameof(SdlcPhase.Done) => SdlcPhase.Done,
            nameof(SdlcPhase.Removed) => SdlcPhase.Removed,
            nameof(SdlcPhase.Unknown) => SdlcPhase.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        actual.ShouldBe(expected, $"SdlcPhase.{name} should be \"{expected}\"");
    }

    [Fact]
    public void AllPhases_AreDistinct()
    {
        var phases = new[]
        {
            SdlcPhase.NeedsPlanning,
            SdlcPhase.NeedsSeeding,
            SdlcPhase.ReadyForImplementation,
            SdlcPhase.InProgress,
            SdlcPhase.ReadyForCompletion,
            SdlcPhase.Done,
            SdlcPhase.Removed,
            SdlcPhase.Unknown
        };

        phases.ShouldBeUnique();
    }

    [Fact]
    public void AllPhases_UseLowercaseSnakeCase()
    {
        var phases = new[]
        {
            SdlcPhase.NeedsPlanning,
            SdlcPhase.NeedsSeeding,
            SdlcPhase.ReadyForImplementation,
            SdlcPhase.InProgress,
            SdlcPhase.ReadyForCompletion,
            SdlcPhase.Done,
            SdlcPhase.Removed,
            SdlcPhase.Unknown
        };

        foreach (var phase in phases)
        {
            phase.ShouldBe(phase.ToLowerInvariant(), $"Phase \"{phase}\" should be lowercase");
            phase.ShouldNotContain(" ");
        }
    }
}
