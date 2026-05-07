using System.Text.Json;
using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="ExecutionMode"/> constants + <c>IsValid</c> guard, and
/// JSON round-trip coverage for <see cref="ResolvedRequirementInputs"/> with
/// the PR #4 execution-mode fields populated.
/// </summary>
public sealed class ExecutionModeTests
{
    // ── IsValid ─────────────────────────────────────────────────────────

    [Fact]
    public void IsValid_AcceptsParallel()
    {
        ExecutionMode.IsValid(ExecutionMode.Parallel).ShouldBeTrue();
    }

    [Fact]
    public void IsValid_AcceptsPlanThenImplement()
    {
        ExecutionMode.IsValid(ExecutionMode.PlanThenImplement).ShouldBeTrue();
    }

    [Fact]
    public void IsValid_RejectsUnknown()
    {
        ExecutionMode.IsValid("serial").ShouldBeFalse();
        ExecutionMode.IsValid("PARALLEL").ShouldBeFalse(); // case-sensitive
    }

    [Fact]
    public void IsValid_RejectsNullOrEmpty()
    {
        ExecutionMode.IsValid(null).ShouldBeFalse();
        ExecutionMode.IsValid("").ShouldBeFalse();
        ExecutionMode.IsValid("   ").ShouldBeFalse();
    }

    // ── String constants are stable wire values ─────────────────────────

    [Fact]
    public void Parallel_HasStableWireValue()
    {
        ExecutionMode.Parallel.ShouldBe("parallel");
    }

    [Fact]
    public void PlanThenImplement_HasStableWireValue()
    {
        ExecutionMode.PlanThenImplement.ShouldBe("plan_then_implement");
    }

    // ── JSON round-trip ─────────────────────────────────────────────────

    [Fact]
    public void ResolvedInputs_Serialization_RoundTrips()
    {
        var input = new ResolvedRequirementInputs
        {
            Decomposable = true,
            DecomposableProvenance = ResolutionProvenance.Explicit,
            FacetOrder = ["actionable", "implementable"],
            FacetOrderProvenance = ResolutionProvenance.Explicit,
            ActionableExecutor = "polyphony",
            ActionableExecutorProvenance = ResolutionProvenance.Explicit,
            ExecutionMode = ExecutionMode.PlanThenImplement,
            ExecutionModeProvenance = ResolutionProvenance.Explicit,
        };

        var json = JsonSerializer.Serialize(
            input, PolyphonyJsonContext.Default.ResolvedRequirementInputs);

        // Snake-case property names must survive source-gen serialization.
        json.ShouldContain("\"execution_mode\":\"plan_then_implement\"");
        json.ShouldContain("\"execution_mode_provenance\":\"explicit\"");

        var roundTripped = JsonSerializer.Deserialize(
            json, PolyphonyJsonContext.Default.ResolvedRequirementInputs);

        roundTripped.ShouldNotBeNull();
        roundTripped!.ExecutionMode.ShouldBe(ExecutionMode.PlanThenImplement);
        roundTripped.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Explicit);
        roundTripped.Decomposable.ShouldBeTrue();
        roundTripped.ActionableExecutor.ShouldBe("polyphony");
    }

    [Fact]
    public void ResolvedInputs_DefaultExecutionMode_RoundTrips()
    {
        var input = new ResolvedRequirementInputs
        {
            Decomposable = false,
            DecomposableProvenance = ResolutionProvenance.Inferred,
            FacetOrder = null,
            FacetOrderProvenance = ResolutionProvenance.NotApplicable,
            ActionableExecutor = null,
            ActionableExecutorProvenance = ResolutionProvenance.NotApplicable,
            ExecutionMode = ExecutionMode.Parallel,
            ExecutionModeProvenance = ResolutionProvenance.Default,
        };

        var json = JsonSerializer.Serialize(
            input, PolyphonyJsonContext.Default.ResolvedRequirementInputs);

        json.ShouldContain("\"execution_mode\":\"parallel\"");
        json.ShouldContain("\"execution_mode_provenance\":\"default\"");

        var roundTripped = JsonSerializer.Deserialize(
            json, PolyphonyJsonContext.Default.ResolvedRequirementInputs);

        roundTripped.ShouldNotBeNull();
        roundTripped!.ExecutionMode.ShouldBe(ExecutionMode.Parallel);
        roundTripped.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Default);
    }
}
