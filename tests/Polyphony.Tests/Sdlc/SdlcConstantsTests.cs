using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for the canonical string-constant types in <c>Polyphony.Sdlc</c>.
/// Cheap structural assertions; the constants are the contract.
/// </summary>
public sealed class SdlcConstantsTests
{
    [Theory]
    [InlineData("needed", true)]
    [InlineData("ready", true)]
    [InlineData("fulfilling", true)]
    [InlineData("satisfied", true)]
    [InlineData("Needed", false)]
    [InlineData("done", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Disposition_IsValid(string? value, bool expected)
    {
        Disposition.IsValid(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("plan_authored", true)]
    [InlineData("plan_reviewed", true)]
    [InlineData("plan_promoted", true)]
    [InlineData("children_seeded", true)]
    [InlineData("implementation_merged", true)]
    [InlineData("action_satisfied", true)]
    [InlineData("evidence_accepted", true)]
    [InlineData("plan", false)]
    [InlineData("Plan_Authored", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RequirementKind_IsValid(string? value, bool expected)
    {
        RequirementKind.IsValid(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("plannable", true)]
    [InlineData("actionable", true)]
    [InlineData("implementable", true)]
    [InlineData("planable", false)]
    [InlineData("Plannable", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Facet_IsValid(string? value, bool expected)
    {
        Facet.IsValid(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("polyphony", true)]
    [InlineData("human", true)]
    [InlineData("agent", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ActionableExecutor_IsValid(string? value, bool expected)
    {
        ActionableExecutor.IsValid(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("definitional", true)]
    [InlineData("policy", true)]
    [InlineData("planner_declared", true)]
    [InlineData("declared", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RequirementEdgeSource_IsValid(string? value, bool expected)
    {
        RequirementEdgeSource.IsValid(value).ShouldBe(expected);
    }
}
