using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class WorkItemIdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99999999)]
    public void Parse_PositiveInt_ReturnsWrappedValue(int value)
    {
        var itemId = WorkItemId.Parse(value);

        itemId.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Parse_NonPositive_Throws(int value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => WorkItemId.Parse(value));
    }

    [Fact]
    public void TryParse_Positive_ReturnsTrueAndWrappedValue()
    {
        var ok = WorkItemId.TryParse(5678, out var itemId);

        ok.ShouldBeTrue();
        itemId.Value.ShouldBe(5678);
    }

    [Fact]
    public void TryParse_NonPositive_ReturnsFalseAndDefault()
    {
        var ok = WorkItemId.TryParse(0, out var itemId);

        ok.ShouldBeFalse();
        itemId.ShouldBe(default(WorkItemId));
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = WorkItemId.Parse(7);
        var b = WorkItemId.Parse(7);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
