using Polyphony.Journal.Observers;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal.Observers;

public sealed class ResourceObserverSupportTests
{
    private const int Root = 1234;
    private const int OtherRoot = 9999;

    [Theory]
    [InlineData("feature/1234")]
    [InlineData("plan/1234")]
    [InlineData("plan/1234-5678")]
    [InlineData("mg/1234_data-layer")]
    [InlineData("mg/1234_data-layer_migrations")]
    [InlineData("impl/1234-5678")]
    [InlineData("evidence/1234")]
    [InlineData("evidence/1234-5678")]
    [InlineData("sdlc/root/1234")]
    public void MatchesPolyphonyBranchPattern_recognizes_root_scoped_branches(string branch)
        => ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, branch).ShouldBeTrue();

    [Theory]
    [InlineData("feature/9999")]
    [InlineData("plan/9999")]
    [InlineData("plan/9999-5678")]
    [InlineData("mg/9999_data-layer")]
    [InlineData("impl/9999-5678")]
    [InlineData("evidence/9999")]
    [InlineData("evidence/9999-5678")]
    [InlineData("sdlc/root/9999")]
    public void MatchesPolyphonyBranchPattern_rejects_other_root_branches(string branch)
        => ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, branch).ShouldBeFalse();

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("develop")]
    [InlineData("feature/some-feature")]
    [InlineData("hotfix/12345")]
    [InlineData("sdlc/other/1234")]
    [InlineData("sdlc/root/")]
    [InlineData("sdlcroot/1234")]
    public void MatchesPolyphonyBranchPattern_rejects_non_polyphony_branches(string branch)
        => ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, branch).ShouldBeFalse();

    // Documented gap: orphan `sdlc/root/{child_id}` branches (descendants under
    // the root's hierarchy) are not attributable from the branch name alone.
    // The matcher conservatively rejects them; journaled descendant sdlc/root
    // branches are still caught via the expected-resource path. If AB#3307's
    // pattern↔projection equivalence suite surfaces real incidents, expand
    // the matcher API to accept a hierarchy-resolved id set.
    [Fact]
    public void MatchesPolyphonyBranchPattern_does_not_attribute_descendant_sdlc_root_branches()
    {
        ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, "sdlc/root/5678").ShouldBeFalse();
        ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, $"sdlc/root/{OtherRoot}").ShouldBeFalse();
    }

    // sdlc/root branches never carry the descendant `{r}-{item}` shape (see
    // ResetCommands.Branches.EnumerateRootSdlcBranchesAsync). Guard against a
    // future caller assuming they do.
    [Fact]
    public void MatchesPolyphonyBranchPattern_does_not_match_sdlc_root_with_hyphen_suffix()
        => ResourceObserverSupport.MatchesPolyphonyBranchPattern(Root, "sdlc/root/1234-5678").ShouldBeFalse();
}
