using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Pure-logic tests for <see cref="ScopeGlobMatcher"/>. The verb-level
/// integration is covered in
/// <see cref="Polyphony.Tests.Commands.PlanCommandsValidateScopeTests"/>.
/// </summary>
public sealed class ScopeGlobMatcherTests
{
    [Theory]
    [InlineData("plans/1100/1101.md", "plans/1100/1101.md", true)]
    [InlineData("plans/1100/1101.md", "plans/1100/1102.md", false)]
    [InlineData("plans/1100/1101.md", "plans/1100/*.md", true)]
    [InlineData("plans/1100/sub/1101.md", "plans/1100/*.md", false)]
    [InlineData("plans/1100/sub/1101.md", "plans/1100/**", true)]
    [InlineData("plans/1100/sub/1101.md", "plans/**", true)]
    [InlineData("plans/1100/sub/1101.md", "**/1101.md", true)]
    [InlineData("plans/1100/sub/1101.md", "**/sub/1101.md", true)]
    [InlineData("plans/1100/sub/1101.md", "**", true)]
    [InlineData("plans/1100/sub/1101.md", "src/**", false)]
    [InlineData("plans/1100/sub/1101.md", "plans/1101/**", false)]
    [InlineData("plans/a.md", "plans/?.md", true)]
    [InlineData("plans/ab.md", "plans/?.md", false)]
    [InlineData("plans/sub/a.md", "plans/?.md", false)]
    public void IsMatch_PosixGlobSemantics(string path, string glob, bool expected)
    {
        ScopeGlobMatcher.IsMatch(path, glob).ShouldBe(expected);
    }

    [Fact]
    public void EmptyPath_NeverMatches()
    {
        ScopeGlobMatcher.IsMatch("", "**").ShouldBeFalse();
    }

    [Fact]
    public void EmptyGlob_NeverMatches()
    {
        ScopeGlobMatcher.IsMatch("plans/a.md", "").ShouldBeFalse();
    }

    [Fact]
    public void IsMatchAny_EmptyGlobList_False()
    {
        ScopeGlobMatcher.IsMatchAny("plans/a.md", Array.Empty<string>()).ShouldBeFalse();
    }

    [Fact]
    public void IsMatchAny_FirstHitWins()
    {
        ScopeGlobMatcher.IsMatchAny("plans/a.md", ["never/**", "plans/*.md"]).ShouldBeTrue();
    }

    [Fact]
    public void DoubleStarAtRoot_MatchesEverything()
    {
        ScopeGlobMatcher.IsMatch("a.md", "**").ShouldBeTrue();
        ScopeGlobMatcher.IsMatch("a/b/c.md", "**").ShouldBeTrue();
    }

    [Fact]
    public void DoubleStarMid_MatchesZeroOrMoreSegments()
    {
        ScopeGlobMatcher.IsMatch("a/b.md", "a/**/b.md").ShouldBeTrue();
        ScopeGlobMatcher.IsMatch("a/x/b.md", "a/**/b.md").ShouldBeTrue();
        ScopeGlobMatcher.IsMatch("a/x/y/b.md", "a/**/b.md").ShouldBeTrue();
    }

    [Fact]
    public void CaseSensitive()
    {
        ScopeGlobMatcher.IsMatch("Plans/a.md", "plans/*.md").ShouldBeFalse();
    }
}
