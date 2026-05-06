using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for the lifecycle ordering helpers on <see cref="Disposition"/>:
/// <see cref="Disposition.Order"/> and <see cref="Disposition.Meets"/>. These
/// underpin <see cref="RequirementSetReducer"/>'s threshold semantics, so any
/// silent regression here would corrupt all downstream readiness computation.
/// </summary>
public sealed class DispositionOrderTests
{
    [Fact]
    public void Order_HasExpectedLifecycleRanking()
    {
        Disposition.Order(Disposition.Needed).ShouldBe(0);
        Disposition.Order(Disposition.Ready).ShouldBe(1);
        Disposition.Order(Disposition.Fulfilling).ShouldBe(2);
        Disposition.Order(Disposition.Satisfied).ShouldBe(3);
    }

    [Fact]
    public void Order_IsStrictlyMonotonic()
    {
        var ranks = new[]
        {
            Disposition.Order(Disposition.Needed),
            Disposition.Order(Disposition.Ready),
            Disposition.Order(Disposition.Fulfilling),
            Disposition.Order(Disposition.Satisfied),
        };

        for (var i = 1; i < ranks.Length; i++)
        {
            ranks[i].ShouldBeGreaterThan(ranks[i - 1]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("READY")]   // case-sensitive: lowercase only
    [InlineData("done")]    // not in the canonical four
    public void Order_ThrowsArgumentOutOfRange_ForUnknownInput(string disposition)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Disposition.Order(disposition));
    }

    [Fact]
    public void Meets_TrueWhenCurrentEqualsThreshold()
    {
        Disposition.Meets(Disposition.Needed, Disposition.Needed).ShouldBeTrue();
        Disposition.Meets(Disposition.Ready, Disposition.Ready).ShouldBeTrue();
        Disposition.Meets(Disposition.Fulfilling, Disposition.Fulfilling).ShouldBeTrue();
        Disposition.Meets(Disposition.Satisfied, Disposition.Satisfied).ShouldBeTrue();
    }

    [Fact]
    public void Meets_TrueWhenCurrentExceedsThreshold()
    {
        Disposition.Meets(Disposition.Satisfied, Disposition.Needed).ShouldBeTrue();
        Disposition.Meets(Disposition.Fulfilling, Disposition.Ready).ShouldBeTrue();
        Disposition.Meets(Disposition.Ready, Disposition.Needed).ShouldBeTrue();
    }

    [Fact]
    public void Meets_FalseWhenCurrentBelowThreshold()
    {
        Disposition.Meets(Disposition.Needed, Disposition.Ready).ShouldBeFalse();
        Disposition.Meets(Disposition.Ready, Disposition.Satisfied).ShouldBeFalse();
        Disposition.Meets(Disposition.Fulfilling, Disposition.Satisfied).ShouldBeFalse();
    }
}
