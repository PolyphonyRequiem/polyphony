using Polyphony.Infrastructure.Worktrees;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Worktrees;

public sealed class BranchSlugTests
{
    // ─── feature/{r} ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_FeatureRoot_ParsesAndSlugifies()
    {
        BranchSlug.TryParse("feature/3085", out var parsed, out var rejection).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Feature);
        parsed.RootId.ShouldBe(3085);
        parsed.Slug.ShouldBe("feature-3085");
        parsed.ItemId.ShouldBeNull();
        parsed.MgSegments.ShouldBeNull();
        rejection.ShouldBeNull();
    }

    [Fact]
    public void TryParse_FeatureNonNumericRoot_Rejects()
    {
        BranchSlug.TryParse("feature/foo", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("positive int");
    }

    // ─── plan/{r} and plan/{r}-{item} ─────────────────────────────────────

    [Fact]
    public void TryParse_PlanRoot_Parses()
    {
        BranchSlug.TryParse("plan/3085", out var parsed, out _).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Plan);
        parsed.RootId.ShouldBe(3085);
        parsed.ItemId.ShouldBeNull();
        parsed.Slug.ShouldBe("plan-3085");
    }

    [Fact]
    public void TryParse_PlanDescendant_Parses()
    {
        BranchSlug.TryParse("plan/3085-3072", out var parsed, out _).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Plan);
        parsed.RootId.ShouldBe(3085);
        parsed.ItemId.ShouldBe(3072);
        parsed.Slug.ShouldBe("plan-3085-3072");
    }

    [Fact]
    public void TryParse_PlanWithLetterItem_Rejects()
    {
        BranchSlug.TryParse("plan/3085-foo", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("positive ints");
    }

    // ─── impl/{r}-{item} ──────────────────────────────────────────────────

    [Fact]
    public void TryParse_Impl_Parses()
    {
        BranchSlug.TryParse("impl/3085-3072", out var parsed, out _).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Impl);
        parsed.RootId.ShouldBe(3085);
        parsed.ItemId.ShouldBe(3072);
        parsed.Slug.ShouldBe("impl-3085-3072");
    }

    [Fact]
    public void TryParse_ImplWithoutItem_Rejects()
    {
        BranchSlug.TryParse("impl/3085", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("impl/{root}-{item}");
    }

    // ─── evidence/{r}-{item} ──────────────────────────────────────────────

    [Fact]
    public void TryParse_Evidence_Parses()
    {
        BranchSlug.TryParse("evidence/3085-3072", out var parsed, out _).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Evidence);
        parsed.RootId.ShouldBe(3085);
        parsed.ItemId.ShouldBe(3072);
        parsed.Slug.ShouldBe("evidence-3085-3072");
    }

    // ─── mg/{r}_{mg_path} ─────────────────────────────────────────────────

    [Fact]
    public void TryParse_MgTopLevel_Parses()
    {
        BranchSlug.TryParse("mg/3085_pg-foo", out var parsed, out _).ShouldBeTrue();
        parsed!.Kind.ShouldBe(BranchKind.Mg);
        parsed.RootId.ShouldBe(3085);
        parsed.MgSegments.ShouldBe(["pg-foo"]);
        parsed.Slug.ShouldBe("mg-3085_pg-foo");
    }

    [Fact]
    public void TryParse_MgNested_ParsesAllSegments()
    {
        BranchSlug.TryParse("mg/3085_outer_inner_leaf", out var parsed, out _).ShouldBeTrue();
        parsed!.MgSegments.ShouldBe(["outer", "inner", "leaf"]);
        parsed.Slug.ShouldBe("mg-3085_outer_inner_leaf");
    }

    [Fact]
    public void TryParse_MgIdStartingWithDigit_Rejects()
    {
        BranchSlug.TryParse("mg/3085_2nd-pg", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("violates");
    }

    [Fact]
    public void TryParse_MgIdWithUppercase_Rejects()
    {
        BranchSlug.TryParse("mg/3085_PG-foo", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("violates");
    }

    [Fact]
    public void TryParse_MgIdWithUnderscoreInSegment_Splits()
    {
        // Underscore is the segment separator; a value like `pg_foo` is
        // parsed as two MG segments ["pg", "foo"], not a single segment
        // containing an underscore. The grammar forbids `_` inside ids.
        BranchSlug.TryParse("mg/3085_pg_foo", out var parsed, out _).ShouldBeTrue();
        parsed!.MgSegments.ShouldBe(["pg", "foo"]);
    }

    [Fact]
    public void TryParse_MgIdTooLong_Rejects()
    {
        var longId = new string('a', 32);  // 32 chars, max is 31 (1 letter + 30)
        BranchSlug.TryParse($"mg/3085_{longId}", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("violates");
    }

    [Fact]
    public void TryParse_MgWithoutMgPath_Rejects()
    {
        BranchSlug.TryParse("mg/3085_", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("mg/{root}");
    }

    [Fact]
    public void TryParse_MgWithDashRootSeparator_Rejects()
    {
        // `mg/3085-pg-foo` would conflict with impl-style root-item
        // payload; the model uses `_` as the MG separator specifically to
        // avoid that ambiguity.
        BranchSlug.TryParse("mg/3085-pg-foo", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("mg/{root}");
    }

    // ─── Hostile inputs ───────────────────────────────────────────────────

    [Fact]
    public void TryParse_PathTraversal_Rejects()
    {
        BranchSlug.TryParse("feature/../etc", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("forbidden");
    }

    [Fact]
    public void TryParse_Backslash_Rejects()
    {
        BranchSlug.TryParse("feature\\3085", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("forbidden");
    }

    [Fact]
    public void TryParse_NullByte_Rejects()
    {
        BranchSlug.TryParse("feature/3085\0evil", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("forbidden");
    }

    [Fact]
    public void TryParse_EmptyBranch_Rejects()
    {
        BranchSlug.TryParse("", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("empty");
    }

    [Fact]
    public void TryParse_NoPrefix_Rejects()
    {
        BranchSlug.TryParse("3085", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("non-empty prefix");
    }

    [Fact]
    public void TryParse_UnknownPrefix_Rejects()
    {
        BranchSlug.TryParse("hotfix/3085", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("unknown branch prefix");
    }

    [Fact]
    public void TryParse_MultipleSlashes_Rejects()
    {
        BranchSlug.TryParse("feature/3085/extra", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("multiple '/'");
    }

    [Fact]
    public void TryParse_LeadingZero_Rejects()
    {
        BranchSlug.TryParse("feature/03085", out _, out var rejection).ShouldBeFalse();
        rejection!.ShouldContain("positive int");
    }

    [Fact]
    public void TryParse_NegativeRoot_Rejects()
    {
        BranchSlug.TryParse("feature/-3085", out _, out var rejection).ShouldBeFalse();
        // `-3085` parses as plan-style payload "" + "3085"; gets rejected
        // either way. Just assert rejection.
        rejection.ShouldNotBeNull();
    }
}
