using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

/// <summary>
/// "Executable copy of the ADR" tests per the Rev 4 critique. Every Rev 4
/// grammar example must round-trip Builder → string → Parser → equal value
/// → Builder. If any of these tests fail, either the grammar drifted or
/// the implementation did.
/// </summary>
public sealed class BranchGrammarRoundTripTests
{
    private sealed record Golden(BranchName Built, ParsedBranch Expected, string ScenarioName);

    private static IReadOnlyList<Golden> AllGoldens()
    {
        var rootId = RootId.Parse(1234);
        var itemId = WorkItemId.Parse(5678);
        var evidenceItemId = WorkItemId.Parse(9999);

        var auth = MergeGroupId.Parse("auth");
        var nestedPath = MergeGroupPath.Of(
            MergeGroupId.Parse("data-layer"),
            MergeGroupId.Parse("migrations"),
            MergeGroupId.Parse("schema"));
        var hyphenInternal = MergeGroupPath.Top1(MergeGroupId.Parse("data-layer-migrations"));

        return new List<Golden>
        {
            new(
                BranchNameBuilder.Feature(rootId),
                new ParsedBranch.Feature(BranchName.CreateUnsafe("feature/1234"), rootId),
                "feature/1234"),
            new(
                BranchNameBuilder.RootPlan(rootId),
                new ParsedBranch.RootPlan(BranchName.CreateUnsafe("plan/1234"), rootId),
                "plan/1234"),
            new(
                BranchNameBuilder.DescendantPlan(rootId, itemId),
                new ParsedBranch.DescendantPlan(BranchName.CreateUnsafe("plan/1234-5678"), rootId, itemId),
                "plan/1234-5678"),
            new(
                BranchNameBuilder.MergeGroup(rootId, MergeGroupPath.Top1(auth)),
                new ParsedBranch.MergeGroup(
                    BranchName.CreateUnsafe("mg/1234_auth"),
                    rootId,
                    MergeGroupPath.Top1(auth)),
                "mg/1234_auth (top-level)"),
            new(
                BranchNameBuilder.MergeGroup(rootId, nestedPath),
                new ParsedBranch.MergeGroup(
                    BranchName.CreateUnsafe("mg/1234_data-layer_migrations_schema"),
                    rootId,
                    nestedPath),
                "mg/1234_data-layer_migrations_schema (nested)"),
            new(
                BranchNameBuilder.MergeGroup(rootId, hyphenInternal),
                new ParsedBranch.MergeGroup(
                    BranchName.CreateUnsafe("mg/1234_data-layer-migrations"),
                    rootId,
                    hyphenInternal),
                "mg/1234_data-layer-migrations (Rev 3 collision case, now safe)"),
            new(
                BranchNameBuilder.Task(rootId, itemId),
                new ParsedBranch.Task(BranchName.CreateUnsafe("task/1234-5678"), rootId, itemId),
                "task/1234-5678"),
            new(
                BranchNameBuilder.Evidence(rootId, evidenceItemId),
                new ParsedBranch.Evidence(BranchName.CreateUnsafe("evidence/1234-9999"), rootId, evidenceItemId),
                "evidence/1234-9999"),
        };
    }

    [Fact]
    public void Builder_Then_Parser_RoundTripsToExpectedDuCase()
    {
        foreach (var golden in AllGoldens())
        {
            var parsed = BranchNameParser.ParseOrUnrecognized(golden.Built.Value);

            parsed.ShouldBe(
                golden.Expected,
                customMessage: $"scenario: {golden.ScenarioName} — built '{golden.Built.Value}'");
        }
    }

    [Fact]
    public void Parser_Then_Builder_RoundTripsToOriginalString()
    {
        foreach (var golden in AllGoldens())
        {
            var parsed = BranchNameParser.ParseOrUnrecognized(golden.Built.Value);

            var rebuilt = parsed switch
            {
                ParsedBranch.Feature f => BranchNameBuilder.Feature(f.RootId),
                ParsedBranch.RootPlan rp => BranchNameBuilder.RootPlan(rp.RootId),
                ParsedBranch.DescendantPlan dp => BranchNameBuilder.DescendantPlan(dp.RootId, dp.ItemId),
                ParsedBranch.MergeGroup mg => BranchNameBuilder.MergeGroup(mg.RootId, mg.Path),
                ParsedBranch.Task t => BranchNameBuilder.Task(t.RootId, t.ItemId),
                ParsedBranch.Evidence e => BranchNameBuilder.Evidence(e.RootId, e.ItemId),
                ParsedBranch.Unrecognized u => throw new InvalidOperationException(
                    $"Builder round-trip not defined for Unrecognized: {u.Raw}"),
                _ => throw new InvalidOperationException("Non-exhaustive ParsedBranch switch — new case added?"),
            };

            rebuilt.Value.ShouldBe(
                golden.Built.Value,
                customMessage: $"scenario: {golden.ScenarioName}");
        }
    }
}

