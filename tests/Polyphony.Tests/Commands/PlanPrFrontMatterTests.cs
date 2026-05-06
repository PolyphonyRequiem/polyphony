using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Unit tests for the front-matter parser used by
/// <c>polyphony pr poll-status --include-metadata</c>. Exercised
/// independently of the verb to make wire-format edge cases easy to
/// pin down (and to keep the verb test suite focused on routing/IO).
/// </summary>
public sealed class PlanPrFrontMatterTests
{
    [Fact]
    public void Parse_EmptyBody_ReturnsDefaults()
    {
        var meta = PlanPrFrontMatter.Parse("");
        meta.RequestsParentChange.ShouldBeFalse();
        meta.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_NoFence_ReturnsDefaults()
    {
        var meta = PlanPrFrontMatter.Parse("This is just a regular PR body without front-matter.");
        meta.RequestsParentChange.ShouldBeFalse();
        meta.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_FenceNotAtStart_ReturnsDefaults()
    {
        // Conservative: only first-thing-in-body fence counts. A fence later
        // in the body is treated as content, not metadata.
        var body = "Some intro text.\n\n---\nrequests_parent_change: true\n---\n";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeFalse();
    }

    [Fact]
    public void Parse_RequestsParentChangeTrue_ReturnsTrue()
    {
        var body = "---\nrequests_parent_change: true\n---\n\n## Plan body";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeTrue();
        meta.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_RequestsParentChangeFalse_ReturnsFalse()
    {
        var body = "---\nrequests_parent_change: false\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeFalse();
    }

    [Fact]
    public void Parse_RequestsParentChangeMissing_ReturnsFalse()
    {
        var body = "---\nancestor_plan_generations:\n  root: 2\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeFalse();
    }

    [Fact]
    public void Parse_AncestorGenerations_ParsesAllEntries()
    {
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 2\n  \"5678\": 1\n  \"99\": 3\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.AncestorPlanGenerations.Count.ShouldBe(3);
        meta.AncestorPlanGenerations["root"].ShouldBe(2);
        meta.AncestorPlanGenerations["5678"].ShouldBe(1);
        meta.AncestorPlanGenerations["99"].ShouldBe(3);
    }

    [Fact]
    public void Parse_AncestorGenerationsEmpty_ReturnsEmptyMap()
    {
        // YAML "{}" inline mapping for the empty case.
        var body = "---\nancestor_plan_generations: {}\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_AncestorGenerationsNonIntValues_SkipsBadEntries()
    {
        // The "main" branch entry is a string and should be skipped — the
        // valid root entry should still come through.
        var body = "---\nancestor_plan_generations:\n  root: 5\n  bad: \"not-a-number\"\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.AncestorPlanGenerations.Count.ShouldBe(1);
        meta.AncestorPlanGenerations["root"].ShouldBe(5);
    }

    [Fact]
    public void Parse_MalformedYaml_ReturnsDefaults()
    {
        // Unbalanced quotes — YamlDotNet should throw, parser should swallow.
        var body = "---\nrequests_parent_change: \"unbalanced\nancestor: oops\n---";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeFalse();
        meta.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_FrontMatterWithTrailingBody_OnlyReadsFrontMatter()
    {
        // The body after the fence mustn't leak in — even if it has
        // YAML-looking content.
        var body = "---\nrequests_parent_change: true\n---\n\n## Plan\n\nrequests_parent_change: false (in prose)\n";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeTrue();
    }

    [Fact]
    public void Parse_CrlfLineEndings_HandledCorrectly()
    {
        // PR bodies edited in GitHub's web UI on Windows produce CRLF.
        var body = "---\r\nrequests_parent_change: true\r\nancestor_plan_generations:\r\n  root: 1\r\n---\r\n";
        var meta = PlanPrFrontMatter.Parse(body);
        meta.RequestsParentChange.ShouldBeTrue();
        meta.AncestorPlanGenerations["root"].ShouldBe(1);
    }
}
