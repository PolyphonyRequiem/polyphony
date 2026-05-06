using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Tagging;

/// <summary>
/// Truth table for <see cref="PolyphonyTags.IsInScope"/> and
/// <see cref="PolyphonyTags.IsRoot"/>. The interesting axis is the
/// disjoint-tag rule: bare <c>polyphony</c> is in-scope but not root,
/// <c>polyphony:root</c> is BOTH (root implies in-scope), and
/// <c>polyphony:planned</c> alone is neither (it's a status sub-tag).
/// </summary>
public sealed class PolyphonyTagsTests
{
    [Theory]
    [InlineData("", false, false)]
    [InlineData("polyphony", true, false)]
    [InlineData("polyphony:root", true, true)]
    [InlineData("polyphony:planned", false, false)]
    [InlineData("polyphony; twig", true, false)]
    [InlineData("polyphony:root; polyphony", true, true)]
    [InlineData("polyphony; polyphony:planned", true, false)]
    [InlineData("twig; polyphony:planned", false, false)]
    public void TruthTable(string tagsRaw, bool expectInScope, bool expectRoot)
    {
        var tags = TagSet.Parse(tagsRaw);
        PolyphonyTags.IsInScope(tags).ShouldBe(expectInScope);
        PolyphonyTags.IsRoot(tags).ShouldBe(expectRoot);
    }

    [Fact]
    public void Constants_MatchSpec()
    {
        // Constants are part of the JSON contract — pin them explicitly.
        PolyphonyTags.InScope.ShouldBe("polyphony");
        PolyphonyTags.Root.ShouldBe("polyphony:root");
        PolyphonyTags.Planned.ShouldBe("polyphony:planned");
    }

    [Fact]
    public void IsInScope_IsCaseInsensitive()
    {
        var upper = TagSet.Parse("POLYPHONY");
        PolyphonyTags.IsInScope(upper).ShouldBeTrue();

        var mixedRoot = TagSet.Parse("Polyphony:Root");
        PolyphonyTags.IsRoot(mixedRoot).ShouldBeTrue();
        PolyphonyTags.IsInScope(mixedRoot).ShouldBeTrue();
    }
}
