using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class BranchNameBuilderTests
{
    [Fact]
    public void Feature_EmitsCanonicalForm()
    {
        var branch = BranchNameBuilder.Feature(RootId.Parse(1234));

        branch.Value.ShouldBe("feature/1234");
    }

    [Fact]
    public void RootPlan_EmitsCanonicalForm()
    {
        var branch = BranchNameBuilder.RootPlan(RootId.Parse(1234));

        branch.Value.ShouldBe("plan/1234");
    }

    [Fact]
    public void DescendantPlan_FlatNaming_EmitsRootDashItem()
    {
        var branch = BranchNameBuilder.DescendantPlan(
            RootId.Parse(1234),
            WorkItemId.Parse(5678));

        // Per Rev 4: descendant plan branches are flat — the hierarchy
        // is captured by the PR's base branch, not the name.
        branch.Value.ShouldBe("plan/1234-5678");
    }

    [Fact]
    public void MergeGroup_TopLevel_EmitsRootUnderscoreId()
    {
        var path = MergeGroupPath.Top1(MergeGroupId.Parse("auth"));
        var branch = BranchNameBuilder.MergeGroup(RootId.Parse(1234), path);

        branch.Value.ShouldBe("mg/1234_auth");
    }

    [Fact]
    public void MergeGroup_Nested_EmitsUnderscoreJoinedPath()
    {
        // The very example from the ADR: mg/1234_data-layer_migrations_schema
        var path = MergeGroupPath.Of(
            MergeGroupId.Parse("data-layer"),
            MergeGroupId.Parse("migrations"),
            MergeGroupId.Parse("schema"));
        var branch = BranchNameBuilder.MergeGroup(RootId.Parse(1234), path);

        branch.Value.ShouldBe("mg/1234_data-layer_migrations_schema");
    }

    [Fact]
    public void MergeGroup_NullPath_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            BranchNameBuilder.MergeGroup(RootId.Parse(1), null!));
    }

    [Fact]
    public void Task_FlatNaming_EmitsRootDashItem()
    {
        var branch = BranchNameBuilder.Impl(
            RootId.Parse(1234),
            WorkItemId.Parse(5678));

        // Per Rev 4: impl branches are flat — PR base records the enclosing MG.
        branch.Value.ShouldBe("impl/1234-5678");
    }

    [Fact]
    public void Evidence_FlatNaming_EmitsRootDashItem()
    {
        var branch = BranchNameBuilder.Evidence(
            RootId.Parse(1234),
            WorkItemId.Parse(9999));

        branch.Value.ShouldBe("evidence/1234-9999");
    }

    [Fact]
    public void EvidenceOrphan_CollapsesToBareItemId()
    {
        // Phase 6 design: when the work item is its own apex the redundant
        // {root}-{item} would just repeat the id, so the builder collapses
        // to evidence/{item}. The collapse decision lives in the verb;
        // the builder just exposes the orphan form for callers that have
        // already decided.
        var branch = BranchNameBuilder.EvidenceOrphan(WorkItemId.Parse(9999));

        branch.Value.ShouldBe("evidence/9999");
    }

    [Fact]
    public void Prefixes_MatchAdrSpecification()
    {
        // Defensive: the ADR's wire grammar pins these prefixes. If anyone
        // changes the constants, this test fails fast instead of waiting
        // for downstream branches to land in production with the wrong
        // namespace.
        BranchNameBuilder.FeaturePrefix.ShouldBe("feature/");
        BranchNameBuilder.PlanPrefix.ShouldBe("plan/");
        BranchNameBuilder.MergeGroupPrefix.ShouldBe("mg/");
        BranchNameBuilder.ImplPrefix.ShouldBe("impl/");
        BranchNameBuilder.EvidencePrefix.ShouldBe("evidence/");
    }
}
