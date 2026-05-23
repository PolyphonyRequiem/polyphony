using Polyphony.Commands;
using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="PlanPrFrontMatter.ParseStrict"/>. The strict
/// variant — used by <c>polyphony pr validate-plan-diff</c> — must
/// distinguish Absent / Malformed / Present so the validator can block
/// a parent-plan touch when the front-matter cannot confirm consent.
/// </summary>
public sealed class PlanPrFrontMatterStrictTests
{
    [Fact]
    public void EmptyBody_IsAbsent()
    {
        var r = PlanPrFrontMatter.ParseStrict(string.Empty);
        r.Status.ShouldBe(FrontMatterStatus.Absent);
        r.RequestsParentChange.ShouldBeFalse();
        r.AncestorPlanGenerations.ShouldBeEmpty();
        r.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public void NoFenceAtStart_IsAbsent()
    {
        var r = PlanPrFrontMatter.ParseStrict("Just a regular PR body.\n\nno front-matter here.");
        r.Status.ShouldBe(FrontMatterStatus.Absent);
        r.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public void FenceLaterInBody_IsAbsent()
    {
        // Conservative: only first-thing-in-body counts as the fence.
        var body = "## Heading\n---\nrequests_parent_change: true\n---\n";
        var r = PlanPrFrontMatter.ParseStrict(body);
        r.Status.ShouldBe(FrontMatterStatus.Absent);
    }

    [Fact]
    public void WhitespaceOnlyBetweenFences_IsAbsent()
    {
        // Empty document — no YAML mapping, treat as no front-matter.
        var body = "---\n   \n---\n";
        var r = PlanPrFrontMatter.ParseStrict(body);
        r.Status.ShouldBe(FrontMatterStatus.Absent);
    }

    [Fact]
    public void ValidYaml_BothKeysPresent_IsPresent()
    {
        var body = "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 2\n  \"5678\": 1\n---\n\n## Plan body";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeTrue();
        r.AncestorPlanGenerations.Count.ShouldBe(2);
        r.AncestorPlanGenerations["root"].ShouldBe(2);
        r.AncestorPlanGenerations["5678"].ShouldBe(1);
        r.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public void ValidYaml_OnlyRequestsKey_IsPresent_GenerationsEmpty()
    {
        var body = "---\nrequests_parent_change: false\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeFalse();
        r.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void ValidYaml_OnlyGenerationsKey_IsPresent_FlagFalse()
    {
        var body = "---\nancestor_plan_generations:\n  root: 7\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeFalse();
        r.AncestorPlanGenerations["root"].ShouldBe(7);
    }

    [Fact]
    public void ValidYaml_NoRecognizedKeys_IsPresent_AllDefaults()
    {
        // Sparse but well-formed mapping is still Present.
        var body = "---\nsomething_else: hello\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeFalse();
        r.AncestorPlanGenerations.ShouldBeEmpty();
        r.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public void MalformedYaml_UnbalancedQuotes_IsMalformed_WithErrorDetail()
    {
        var body = "---\nrequests_parent_change: \"unbalanced\nancestor: oops\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.RequestsParentChange.ShouldBeFalse();
        r.AncestorPlanGenerations.ShouldBeEmpty();
        r.ErrorDetail.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void RequestsParentChange_QuotedString_IsMalformed()
    {
        // A quoted "yes" is a YAML string, not a YAML bool. Reject strictly.
        var body = "---\nrequests_parent_change: \"yes\"\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("requests_parent_change");
    }

    [Fact]
    public void AncestorGenerations_AsSequence_IsMalformed()
    {
        var body = "---\nancestor_plan_generations:\n  - root\n  - 1234\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("ancestor_plan_generations");
    }

    [Fact]
    public void AncestorGenerations_AsScalar_IsMalformed()
    {
        var body = "---\nancestor_plan_generations: 42\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("ancestor_plan_generations");
    }

    [Fact]
    public void AncestorGenerations_NonIntValue_IsMalformed()
    {
        var body = "---\nancestor_plan_generations:\n  root: \"two\"\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("integer");
    }

    [Fact]
    public void RootNotMapping_IsMalformed()
    {
        // Top-level scalar is not a mapping — reject.
        var body = "---\njust-a-scalar\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("mapping");
    }

    [Fact]
    public void CrlfLineEndings_AreHandled()
    {
        var body = "---\r\nrequests_parent_change: true\r\nancestor_plan_generations:\r\n  root: 1\r\n---\r\n";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeTrue();
        r.AncestorPlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public void LfLineEndings_AreHandled()
    {
        var body = "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 1\n---\n";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeTrue();
        r.AncestorPlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public void ExplicitYamlBoolTag_IsAccepted()
    {
        // Explicit !!bool tag is also a YAML boolean — accept.
        var body = "---\nrequests_parent_change: !!bool true\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RequestsParentChange.ShouldBeTrue();
    }

    [Fact]
    public void RunId_AbsentKey_RoundTripsAsNull()
    {
        // W7 (AB#3281): legacy plan-PR bodies stamped pre-run_id rollout
        // do not carry the key. Strict parse must accept that shape and
        // surface RunId == null so the validator does not block them.
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RunId.ShouldBeNull();
    }

    [Fact]
    public void RunId_ValidScalar_IsRoundTripped()
    {
        // W7 (AB#3281): well-formed lineage stamp must arrive intact at
        // the call site so a stale-fence reviewer can refuse the merge
        // when the lineage doesn't match the journal's current run.
        var body = "---\nrequests_parent_change: false\nrun_id: 01HZK7Y9ABCDEF0123456789AB\nancestor_plan_generations: {}\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Present);
        r.RunId.ShouldBe("01HZK7Y9ABCDEF0123456789AB");
    }

    [Fact]
    public void RunId_EmptyScalar_IsMalformed()
    {
        // An author who clears run_id has produced a worse signal than
        // omitting it — block the merge so they can either fix or remove.
        var body = "---\nrequests_parent_change: false\nrun_id: \"\"\nancestor_plan_generations: {}\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("run_id");
    }

    [Fact]
    public void RunId_NonScalarMapping_IsMalformed()
    {
        // Shape error: someone tried to nest a structure under run_id.
        var body = "---\nrun_id:\n  nested: oops\n---";
        var r = PlanPrFrontMatter.ParseStrict(body);

        r.Status.ShouldBe(FrontMatterStatus.Malformed);
        r.ErrorDetail!.ShouldContain("run_id");
    }
}
