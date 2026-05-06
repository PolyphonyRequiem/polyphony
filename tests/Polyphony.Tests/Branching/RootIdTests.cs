using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class RootIdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99999999)]
    public void Parse_PositiveInt_ReturnsWrappedValue(int value)
    {
        var rootId = RootId.Parse(value);

        rootId.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Parse_NonPositive_Throws(int value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RootId.Parse(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryParse_NonPositive_ReturnsFalseAndDefault(int value)
    {
        var ok = RootId.TryParse(value, out var rootId);

        ok.ShouldBeFalse();
        rootId.ShouldBe(default(RootId));
    }

    [Fact]
    public void TryParse_Positive_ReturnsTrueAndWrappedValue()
    {
        var ok = RootId.TryParse(2026, out var rootId);

        ok.ShouldBeTrue();
        rootId.Value.ShouldBe(2026);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = RootId.Parse(100);
        var b = RootId.Parse(100);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void ToString_UsesInvariantDecimal()
    {
        // Sanity: even on non-en-US cultures, the integer.ToString invariant
        // form has no thousands separator. We can't easily change culture
        // mid-test in xUnit's default config, but we assert the shape.
        var rootId = RootId.Parse(1234);

        rootId.ToString().ShouldBe("1234");
    }
}
