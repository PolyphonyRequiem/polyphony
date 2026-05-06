using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Per-invariant negative tests for <see cref="RunManifestValidator"/>.
/// Each test mutates a known-good baseline in exactly one way and
/// asserts the validator surfaces the expected issue.
/// </summary>
public sealed class RunManifestValidatorTests
{
    private static RunManifest GoodBaseline() => new()
    {
        Schema = 1,
        RootId = 1234,
        PlatformProject = "dev.azure.com/org/project",
        CreatedAt = new DateTime(2026, 5, 6, 15, 30, 0, DateTimeKind.Utc),
        CreatedBy = "dangreen",
        BranchModelVersion = 1,
        MergeGroups = new List<MergeGroupEntry>
        {
            new()
            {
                Id = "data-layer",
                MgPath = "data-layer",
                Items = new List<int> { 101 },
                Nesting = ManifestNesting.Top,
                Isolation = ManifestIsolation.PerMergeGroup,
            },
        },
    };

    [Fact]
    public void Validate_GoodBaseline_HasNoIssues()
    {
        RunManifestValidator.Validate(GoodBaseline()).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WrongSchema_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.Schema = 99;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("schema"));
    }

    [Fact]
    public void Validate_WrongBranchModelVersion_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.BranchModelVersion = 2;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("branch_model_version"));
    }

    [Fact]
    public void Validate_NonPositiveRootId_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.RootId = 0;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("root_id"));
    }

    [Fact]
    public void Validate_EmptyPlatformProject_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.PlatformProject = "";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("platform_project"));
    }

    [Fact]
    public void Validate_BadMgIdGrammar_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].Id = "BAD!";
        manifest.MergeGroups[0].MgPath = "BAD!";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("MG-id grammar"));
    }

    [Fact]
    public void Validate_FlatSentinelAsId_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].Id = ManifestOverride.Flat;
        manifest.MergeGroups[0].MgPath = ManifestOverride.Flat;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("reserved sentinel"));
    }

    [Fact]
    public void Validate_IdNotEqualToTerminalSegment_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].MgPath = "data-layer_extra";
        manifest.MergeGroups[0].ParentMgPath = "data-layer";
        manifest.MergeGroups[0].Nesting = ManifestNesting.Nested;
        // id stays "data-layer" but terminal becomes "extra"
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("terminal segment"));
    }

    [Fact]
    public void Validate_DuplicateMgPath_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups.Add(new MergeGroupEntry
        {
            Id = "data-layer",
            MgPath = "data-layer",
            Items = new List<int> { 999 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
        });
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("duplicate"));
    }

    [Fact]
    public void Validate_TopLevelWithParentPath_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].ParentMgPath = "something";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("parent_mg_path null"));
    }

    [Fact]
    public void Validate_NestedWithoutParentPath_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].MgPath = "data-layer_inner";
        manifest.MergeGroups[0].Id = "inner";
        manifest.MergeGroups[0].Nesting = ManifestNesting.Nested;
        manifest.MergeGroups[0].ParentMgPath = null;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("non-empty parent_mg_path"));
    }

    [Fact]
    public void Validate_NestedParentPathDoesntMatchPrefix_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].MgPath = "alpha_beta_inner";
        manifest.MergeGroups[0].Id = "inner";
        manifest.MergeGroups[0].Nesting = ManifestNesting.Nested;
        manifest.MergeGroups[0].ParentMgPath = "wrong";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("parent_mg_path"));
    }

    [Fact]
    public void Validate_BadIsolation_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].Isolation = "per-burrito";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("isolation"));
    }

    [Fact]
    public void Validate_BadNesting_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].Nesting = "weird";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("nesting"));
    }

    [Fact]
    public void Validate_BadOverride_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].NestingOverride = "BAD!";
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("nesting_override"));
    }

    [Fact]
    public void Validate_FlatOverride_IsValid()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].NestingOverride = ManifestOverride.Flat;
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NamedOverride_IsValid()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].NestingOverride = "data-migrations";
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NonPositiveItem_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergeGroups[0].Items = new List<int> { 0 };
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("items"));
    }

    [Fact]
    public void Validate_BadPlanGenerationsKey_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.PlanGenerations["abc"] = 1;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("plan_generations"));
    }

    [Fact]
    public void Validate_NegativePlanGenerationsValue_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.PlanGenerations["root"] = -1;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("plan_generations"));
    }

    [Fact]
    public void Validate_RebaseMissingFields_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.Rebases.Add(new RebaseRecord());
        var issues = RunManifestValidator.Validate(manifest);
        issues.ShouldContain(s => s.Contains("rebases"));
    }

    // -- merged_plan_prs --

    private static MergedPlanPrEntry GoodLedgerEntry() => new()
    {
        PrNumber = 42,
        ItemKey = "5678",
        MergeCommit = "abc1234",
        PreviousGeneration = 0,
        CurrentGeneration = 1,
        RecordedAt = new DateTime(2026, 5, 6, 19, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Validate_GoodLedgerEntry_HasNoIssues()
    {
        var manifest = GoodBaseline();
        manifest.MergedPlanPrs.Add(GoodLedgerEntry());
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RootItemKeyLedgerEntry_HasNoIssues()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.ItemKey = "root";
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_LedgerEntryNonPositivePrNumber_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.PrNumber = 0;
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("pr_number"));
    }

    [Fact]
    public void Validate_LedgerDuplicatePrNumber_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.MergedPlanPrs.Add(GoodLedgerEntry());
        var dup = GoodLedgerEntry();
        dup.PreviousGeneration = 1;
        dup.CurrentGeneration = 2;
        manifest.MergedPlanPrs.Add(dup);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("duplicate"));
    }

    [Fact]
    public void Validate_LedgerEmptyItemKey_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.ItemKey = "";
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("item_key"));
    }

    [Fact]
    public void Validate_LedgerNonRootNonNumericItemKey_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.ItemKey = "ROOT";
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("item_key"));
    }

    [Fact]
    public void Validate_LedgerEmptyMergeCommit_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.MergeCommit = "";
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("merge_commit"));
    }

    [Fact]
    public void Validate_LedgerCurrentNotGreaterThanPrevious_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.PreviousGeneration = 5;
        entry.CurrentGeneration = 5;
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("current_generation"));
    }

    [Fact]
    public void Validate_LedgerNegativePreviousGeneration_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.PreviousGeneration = -1;
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("previous_generation"));
    }

    [Fact]
    public void Validate_LedgerMissingRecordedAt_ReportsIssue()
    {
        var manifest = GoodBaseline();
        var entry = GoodLedgerEntry();
        entry.RecordedAt = default;
        manifest.MergedPlanPrs.Add(entry);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("recorded_at"));
    }

    [Fact]
    public void Validate_ApprovalMissingFields_ReportsIssue()
    {
        var manifest = GoodBaseline();
        manifest.HumanApprovals.Add(new HumanApprovalRecord());
        var issues = RunManifestValidator.Validate(manifest);
        issues.ShouldContain(s => s.Contains("human_approvals"));
    }

    [Fact]
    public void ValidateOrThrow_BadManifest_Throws()
    {
        var manifest = GoodBaseline();
        manifest.RootId = 0;
        Should.Throw<InvalidOperationException>(() =>
            RunManifestValidator.ValidateOrThrow(manifest, "test.yaml"))
            .Message.ShouldContain("root_id");
    }

    [Fact]
    public void ValidateOrThrow_GoodManifest_DoesNotThrow()
    {
        Should.NotThrow(() => RunManifestValidator.ValidateOrThrow(GoodBaseline(), "test.yaml"));
    }
}
