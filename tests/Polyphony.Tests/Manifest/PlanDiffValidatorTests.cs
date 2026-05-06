using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Unit tests for <see cref="PlanDiffValidator"/>. Pure-function classifier
/// covering every priority tier in the type-level docs:
/// polyphony-state &gt; ancestor-plan &gt; parent-plan (with sub-rules:
/// flag-missing / front-matter Malformed / front-matter Absent) &gt;
/// flag-set-without-parent-touch warning &gt; ok.
/// </summary>
public sealed class PlanDiffValidatorTests
{
    private const string SelfPlan = "plans/plan-1234.md";
    private const string ParentPlan = "plans/plan-1000.md";

    [Fact]
    public void EmptyChangedPaths_IsOk_WithEmptyBuckets()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: Array.Empty<string>(),
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.Code.ShouldBe("ok");
        result.SelfPlanFiles.ShouldBeEmpty();
        result.ParentPlanFiles.ShouldBeEmpty();
        result.AncestorPlanFiles.ShouldBeEmpty();
        result.PolyphonyStateFiles.ShouldBeEmpty();
        result.OtherFiles.ShouldBeEmpty();
    }

    [Fact]
    public void SelfPlanOnly_IsOk()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.Code.ShouldBe("ok");
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
        result.ParentPlanFiles.ShouldBeEmpty();
        result.OtherFiles.ShouldBeEmpty();
    }

    [Fact]
    public void ParentPlanWithoutFlag_IsBlocking_ChildTouchedParent()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ParentPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_parent_plan");
        result.Message.ShouldContain(ParentPlan);
        result.Message.ShouldContain("requests_parent_change");
        result.ParentPlanFiles.ShouldBe(new[] { ParentPlan });
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
    }

    [Fact]
    public void ParentPlanWithFlag_AndPresentFrontMatter_IsOk()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ParentPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.Code.ShouldBe("ok");
        result.ParentPlanFiles.ShouldBe(new[] { ParentPlan });
    }

    [Fact]
    public void ParentPlanWithFlag_AndAbsentFrontMatter_IsBlocking_MissingFrontMatter()
    {
        // Defensive case — the verb caller passes requestsParentChange=false
        // when status is Absent, but the validator must still classify
        // correctly if asked directly.
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ParentPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("missing_front_matter");
        result.Message.ShouldContain(ParentPlan);
    }

    [Fact]
    public void ParentPlanWithFlag_AndMalformedFrontMatter_IsBlocking_Malformed()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ParentPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Malformed);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("malformed_front_matter");
        result.Message.ShouldContain(ParentPlan);
    }

    [Fact]
    public void AncestorPlanTouched_IsBlocking_RegardlessOfFlag()
    {
        const string ancestor = "plans/plan-100.md";
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ancestor },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: new[] { ancestor },
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_ancestor_plan");
        result.Message.ShouldContain(ancestor);
        result.AncestorPlanFiles.ShouldBe(new[] { ancestor });
    }

    [Fact]
    public void AncestorPlanTouched_WithoutFlag_StillBlocking()
    {
        const string ancestor = "plans/plan-100.md";
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { ancestor },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: new[] { ancestor },
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_ancestor_plan");
        result.AncestorPlanFiles.ShouldBe(new[] { ancestor });
    }

    [Fact]
    public void PolyphonyRunYaml_IsBlocking()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ".polyphony/run.yaml" },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_polyphony_state");
        result.Message.ShouldContain(".polyphony/run.yaml");
        result.PolyphonyStateFiles.ShouldBe(new[] { ".polyphony/run.yaml" });
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
    }

    [Fact]
    public void PolyphonyLockFile_IsBlocking()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { ".polyphony/locks/run-1.lock" },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_polyphony_state");
        result.PolyphonyStateFiles.ShouldBe(new[] { ".polyphony/locks/run-1.lock" });
    }

    [Fact]
    public void PolyphonyState_BeatsAncestorTouch_PriorityOne()
    {
        const string ancestor = "plans/plan-100.md";
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { ancestor, ".polyphony/run.yaml" },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: new[] { ancestor },
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_polyphony_state");
        // Both buckets still populated for the structured report.
        result.PolyphonyStateFiles.ShouldBe(new[] { ".polyphony/run.yaml" });
        result.AncestorPlanFiles.ShouldBe(new[] { ancestor });
    }

    [Fact]
    public void FlagSetButNoParentTouched_IsWarning()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, "src/feature.cs" },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Warning);
        result.Code.ShouldBe("flag_set_no_parent_changes");
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
        result.OtherFiles.ShouldBe(new[] { "src/feature.cs" });
        result.ParentPlanFiles.ShouldBeEmpty();
    }

    [Fact]
    public void RootSelf_OnlySelfAndOther_IsOk()
    {
        // Root plan PR — no parent. Touching only self + other code is fine.
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, "README.md" },
            selfPlanFile: SelfPlan,
            parentPlanFile: null,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.Code.ShouldBe("ok");
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
        result.OtherFiles.ShouldBe(new[] { "README.md" });
    }

    [Fact]
    public void RootSelf_FlagSet_IsWarning_FlagHasNoMeaning()
    {
        // No parent exists, but the flag was set anyway. Surface as the
        // flag-no-effect warning so the operator notices.
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: null,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: true,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Warning);
        result.Code.ShouldBe("flag_set_no_parent_changes");
    }

    [Fact]
    public void SelfPlusOtherCode_IsOk_BucketedCorrectly()
    {
        var changed = new[] { SelfPlan, "src/feature.cs", "tests/feature_test.cs", "docs/usage.md" };
        var result = PlanDiffValidator.Check(
            changedPaths: changed,
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.Code.ShouldBe("ok");
        result.SelfPlanFiles.ShouldBe(new[] { SelfPlan });
        result.OtherFiles.ShouldBe(new[] { "src/feature.cs", "tests/feature_test.cs", "docs/usage.md" });
        result.ParentPlanFiles.ShouldBeEmpty();
        result.AncestorPlanFiles.ShouldBeEmpty();
        result.PolyphonyStateFiles.ShouldBeEmpty();
    }

    [Fact]
    public void MultipleAncestors_AllReported_InMessage()
    {
        var ancestors = new[] { "plans/plan-100.md", "plans/plan-200.md" };
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, ancestors[0], ancestors[1] },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: ancestors,
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_ancestor_plan");
        result.Message.ShouldContain("plans/plan-100.md");
        result.Message.ShouldContain("plans/plan-200.md");
        result.AncestorPlanFiles.Count.ShouldBe(2);
    }

    [Fact]
    public void DuplicatePathsInInput_DedupedAtBucket()
    {
        // Defensive: gh shouldn't emit duplicates, but if it does we treat
        // as a single violation, not multiple.
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { ParentPlan, ParentPlan },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Present);

        result.Severity.ShouldBe(ValidationSeverity.Blocking);
        result.Code.ShouldBe("child_touched_parent_plan");
        result.ParentPlanFiles.Count.ShouldBe(1);
    }

    [Fact]
    public void EmptyBucketsUseArrayEmpty_NotNull()
    {
        var result = PlanDiffValidator.Check(
            changedPaths: Array.Empty<string>(),
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        // Each bucket is non-null and empty (Array.Empty<T>() per spec).
        result.SelfPlanFiles.ShouldNotBeNull();
        result.ParentPlanFiles.ShouldNotBeNull();
        result.AncestorPlanFiles.ShouldNotBeNull();
        result.PolyphonyStateFiles.ShouldNotBeNull();
        result.OtherFiles.ShouldNotBeNull();
    }

    [Fact]
    public void OkResult_PopulatesAllBuckets_StructuredReport()
    {
        // When OK, the per-bucket lists must still be populated for the
        // caller to render a diff summary.
        var result = PlanDiffValidator.Check(
            changedPaths: new[] { SelfPlan, "src/x.cs" },
            selfPlanFile: SelfPlan,
            parentPlanFile: ParentPlan,
            ancestorPlanFiles: Array.Empty<string>(),
            requestsParentChange: false,
            frontMatterStatus: FrontMatterStatus.Absent);

        result.Severity.ShouldBe(ValidationSeverity.None);
        result.SelfPlanFiles.Count.ShouldBe(1);
        result.OtherFiles.Count.ShouldBe(1);
    }
}
