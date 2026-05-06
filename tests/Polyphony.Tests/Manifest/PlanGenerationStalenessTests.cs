using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Tests for <see cref="PlanGenerationStaleness"/>. The helper compares an
/// ancestor-plan-generation snapshot (embedded in a plan-PR body's
/// front-matter) against the live <c>plan_generations</c> ledger from the
/// run manifest. Three observable outcomes — Fresh, Stale, Empty — and the
/// caller (the <c>pr merge-plan-pr</c> verb) decides what to do per
/// outcome and per role (root vs descendant).
/// </summary>
public sealed class PlanGenerationStalenessTests
{
    /// <summary>Empty snapshot is Empty (root plan or hand-opened PR pre P3).</summary>
    [Fact]
    public void Empty_Snapshot_Returns_IsEmpty()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int>(),
            currentGenerations: new Dictionary<string, int> { ["root"] = 1 });

        result.IsEmpty.ShouldBeTrue();
        result.IsStale.ShouldBeFalse();
        result.StaleEntries.ShouldBeEmpty();
    }

    /// <summary>Snapshot matches manifest exactly → Fresh.</summary>
    [Fact]
    public void Matching_Snapshot_Is_Fresh()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["root"] = 2, ["1100"] = 1 },
            currentGenerations: new Dictionary<string, int> { ["root"] = 2, ["1100"] = 1 });

        result.IsEmpty.ShouldBeFalse();
        result.IsStale.ShouldBeFalse();
        result.StaleEntries.ShouldBeEmpty();
    }

    /// <summary>Ancestor advanced past snapshot → Stale, with diff entry.</summary>
    [Fact]
    public void Ancestor_Advanced_Is_Stale()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["root"] = 1 },
            currentGenerations: new Dictionary<string, int> { ["root"] = 3 });

        result.IsStale.ShouldBeTrue();
        result.IsEmpty.ShouldBeFalse();
        result.StaleEntries.Count.ShouldBe(1);
        var entry = result.StaleEntries[0];
        entry.AncestorKey.ShouldBe("root");
        entry.SnapshotGeneration.ShouldBe(1);
        entry.CurrentGeneration.ShouldBe(3);
    }

    /// <summary>
    /// Snapshot key not in manifest is treated as current=0. Snapshot value=0
    /// → no diff (matches). Snapshot value &gt;0 → stale (manifest doesn't
    /// agree with the claimed advance — corruption signal).
    /// </summary>
    [Fact]
    public void Missing_Manifest_Key_With_Snapshot_Zero_Is_Fresh()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["1100"] = 0 },
            currentGenerations: new Dictionary<string, int>());

        result.IsStale.ShouldBeFalse();
    }

    /// <summary>
    /// Snapshot claims an advance (gen &gt; 0) the manifest never recorded.
    /// Surfaced as stale so the operator investigates rather than letting
    /// an inconsistent merge through.
    /// </summary>
    [Fact]
    public void Snapshot_Claims_Unrecorded_Advance_Is_Stale()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["1100"] = 2 },
            currentGenerations: new Dictionary<string, int>());

        result.IsStale.ShouldBeTrue();
        result.StaleEntries.Count.ShouldBe(1);
        result.StaleEntries[0].SnapshotGeneration.ShouldBe(2);
        result.StaleEntries[0].CurrentGeneration.ShouldBe(0);
    }

    /// <summary>Multiple stale entries are collected and reported together.</summary>
    [Fact]
    public void Multiple_Stale_Entries_All_Reported()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["root"] = 1, ["1100"] = 0, ["1200"] = 2 },
            currentGenerations: new Dictionary<string, int> { ["root"] = 2, ["1100"] = 0, ["1200"] = 5 });

        result.IsStale.ShouldBeTrue();
        result.StaleEntries.Count.ShouldBe(2);
        result.StaleEntries.ShouldContain(e => e.AncestorKey == "root" && e.CurrentGeneration == 2);
        result.StaleEntries.ShouldContain(e => e.AncestorKey == "1200" && e.CurrentGeneration == 5);
    }

    /// <summary>
    /// A snapshot can be partially fresh — only the entries that have
    /// drifted are surfaced; matching entries don't appear in the diff.
    /// </summary>
    [Fact]
    public void Mixed_Snapshot_Reports_Only_Stale_Entries()
    {
        var result = PlanGenerationStaleness.Check(
            snapshot: new Dictionary<string, int> { ["root"] = 1, ["1100"] = 1 },
            currentGenerations: new Dictionary<string, int> { ["root"] = 1, ["1100"] = 2 });

        result.IsStale.ShouldBeTrue();
        result.StaleEntries.Count.ShouldBe(1);
        result.StaleEntries[0].AncestorKey.ShouldBe("1100");
    }

    /// <summary>FormatStaleEntries renders empty list as "(none)".</summary>
    [Fact]
    public void FormatStaleEntries_Empty_Is_None()
    {
        PlanGenerationStaleness.FormatStaleEntries([]).ShouldBe("(none)");
    }

    /// <summary>FormatStaleEntries renders a comma-separated diff.</summary>
    [Fact]
    public void FormatStaleEntries_Renders_Diff()
    {
        var entries = new[]
        {
            new PlanGenerationStaleness.StaleEntry("root", 1, 3),
            new PlanGenerationStaleness.StaleEntry("1100", 0, 2),
        };

        var formatted = PlanGenerationStaleness.FormatStaleEntries(entries);

        formatted.ShouldContain("root: snapshot=1, current=3");
        formatted.ShouldContain("1100: snapshot=0, current=2");
    }
}
