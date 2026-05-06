using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class BranchNameParserTests
{
    [Fact]
    public void ParseOrUnrecognized_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => BranchNameParser.ParseOrUnrecognized(null!));
    }

    [Fact]
    public void ParseOrUnrecognized_Feature_ReturnsFeatureCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("feature/1234");

        var feature = parsed.ShouldBeOfType<ParsedBranch.Feature>();
        feature.RootId.Value.ShouldBe(1234);
        feature.Branch.Value.ShouldBe("feature/1234");
    }

    [Fact]
    public void ParseOrUnrecognized_RootPlan_ReturnsRootPlanCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("plan/1234");

        var rootPlan = parsed.ShouldBeOfType<ParsedBranch.RootPlan>();
        rootPlan.RootId.Value.ShouldBe(1234);
    }

    [Fact]
    public void ParseOrUnrecognized_DescendantPlan_ReturnsDescendantPlanCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("plan/1234-5678");

        var plan = parsed.ShouldBeOfType<ParsedBranch.DescendantPlan>();
        plan.RootId.Value.ShouldBe(1234);
        plan.ItemId.Value.ShouldBe(5678);
    }

    [Fact]
    public void ParseOrUnrecognized_TopMergeGroup_ReturnsMergeGroupCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("mg/1234_auth");

        var mg = parsed.ShouldBeOfType<ParsedBranch.MergeGroup>();
        mg.RootId.Value.ShouldBe(1234);
        mg.Path.IsTopLevel.ShouldBeTrue();
        mg.Path.Top.Value.ShouldBe("auth");
    }

    [Fact]
    public void ParseOrUnrecognized_NestedMergeGroup_RetainsAllSegments()
    {
        // The exact ADR example that motivated Rev 4's `_` delimiter.
        var parsed = BranchNameParser.ParseOrUnrecognized("mg/1234_data-layer_migrations_schema");

        var mg = parsed.ShouldBeOfType<ParsedBranch.MergeGroup>();
        mg.RootId.Value.ShouldBe(1234);
        mg.Path.Depth.ShouldBe(3);
        mg.Path.Segments.Select(s => s.Value).ShouldBe(["data-layer", "migrations", "schema"]);
        mg.Path.Canonical.ShouldBe("data-layer_migrations_schema");
    }

    [Fact]
    public void ParseOrUnrecognized_HyphenInsideMgIdSegment_DoesNotConfuseDelimiter()
    {
        // The collision Rev 3 had: data-layer-migrations parses unambiguously
        // as a single top-level MG named `data-layer-migrations` because
        // the `_` (not `-`) is the hierarchy delimiter.
        var parsed = BranchNameParser.ParseOrUnrecognized("mg/1234_data-layer-migrations");

        var mg = parsed.ShouldBeOfType<ParsedBranch.MergeGroup>();
        mg.Path.IsTopLevel.ShouldBeTrue();
        mg.Path.Top.Value.ShouldBe("data-layer-migrations");
    }

    [Fact]
    public void ParseOrUnrecognized_Task_ReturnsTaskCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("task/1234-5678");

        var task = parsed.ShouldBeOfType<ParsedBranch.Task>();
        task.RootId.Value.ShouldBe(1234);
        task.ItemId.Value.ShouldBe(5678);
    }

    [Fact]
    public void ParseOrUnrecognized_Evidence_ReturnsEvidenceCase()
    {
        var parsed = BranchNameParser.ParseOrUnrecognized("evidence/1234-9999");

        var evidence = parsed.ShouldBeOfType<ParsedBranch.Evidence>();
        evidence.RootId.Value.ShouldBe(1234);
        evidence.ItemId.Value.ShouldBe(9999);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/v1")]
    [InlineData("dev/dangreen/something")]
    [InlineData("sdlc/branch-foundation")]
    [InlineData("")]
    [InlineData("feature/")]                          // missing root
    [InlineData("feature/abc")]                       // non-numeric root
    [InlineData("feature/0")]                         // zero (not positive)
    [InlineData("feature/01")]                        // leading zero (rejected for canonicality)
    [InlineData("feature/-5")]                        // signed
    [InlineData("feature/1234-slug")]                 // Rev 3 / pre-Rev-4 form with slug
    [InlineData("plan/1234/extra")]                   // extra path segment
    [InlineData("plan/")]                             // truncated
    [InlineData("plan/1234-")]                        // trailing dash, no item
    [InlineData("plan/-1")]                           // signed item
    [InlineData("mg/")]                               // truncated
    [InlineData("mg/1234")]                           // missing path
    [InlineData("mg/1234_")]                          // empty path
    [InlineData("mg/1234_Auth")]                      // uppercase segment
    [InlineData("mg/1234_auth_")]                     // trailing _
    [InlineData("mg/1234__auth")]                     // empty segment
    [InlineData("mg/0_auth")]                         // zero root
    [InlineData("task/1234")]                         // missing item
    [InlineData("task/1234-5678-9012")]               // extra segment (Rev 3 form would be plausible here)
    [InlineData("evidence/1234")]                     // missing item
    [InlineData("MG/1234_auth")]                      // uppercase prefix
    [InlineData(" task/1234-5678")]                   // leading whitespace
    [InlineData("task/1234-5678 ")]                   // trailing whitespace
    public void ParseOrUnrecognized_NonGrammar_ReturnsUnrecognized(string raw)
    {
        var parsed = BranchNameParser.ParseOrUnrecognized(raw);

        var unrecognized = parsed.ShouldBeOfType<ParsedBranch.Unrecognized>();
        unrecognized.Raw.ShouldBe(raw);
    }

    [Theory]
    [InlineData("feature/1234")]
    [InlineData("plan/1234")]
    [InlineData("plan/1234-5678")]
    [InlineData("mg/1234_auth")]
    [InlineData("mg/1234_data-layer_migrations_schema")]
    [InlineData("task/1234-5678")]
    [InlineData("evidence/1234-9999")]
    public void TryParse_Recognized_ReturnsTrue(string raw)
    {
        var ok = BranchNameParser.TryParse(raw, out var parsed);

        ok.ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.ShouldNotBeOfType<ParsedBranch.Unrecognized>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("feature/abc")]
    public void TryParse_Unrecognized_ReturnsFalseAndNull(string? raw)
    {
        var ok = BranchNameParser.TryParse(raw, out var parsed);

        ok.ShouldBeFalse();
        parsed.ShouldBeNull();
    }
}
