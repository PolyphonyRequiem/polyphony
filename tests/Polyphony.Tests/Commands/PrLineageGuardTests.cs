using Polyphony.Commands;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W9 (AB#3282): unit tests for the foreign-PR firewall. Exercise
/// the helper directly so the merge-verb integration tests don't
/// have to enumerate every body/journal combination — the verbs
/// stack one well-known case on top of this helper's matrix.
/// </summary>
public sealed class PrLineageGuardTests
{
    private const string CurrentRunId = "01HZK7CURRENT0000000000000";
    private const string OtherRunId = "01HZK7OTHER00000000000000A";

    private static IJournalStore NullJournal() => new NullJournalStore();

    [Fact]
    public async Task ManualLineage_AllowsWithSkipSource()
    {
        // A `manual_*` lineage is a "we don't actually know who we are"
        // marker; refusing every merge under it would brick local dev.
        var d = await PrLineageGuard.CheckAsync(
            body: null, currentRunId: CurrentRunId,
            isManualLineage: true, bodyHasFrontMatter: false,
            journal: NullJournal(),
            journalAction: "pr_open_impl_pr",
            journalTarget: "branchpair:impl/1-2→mg/1_x",
            ct: default);
        d.Allowed.ShouldBeTrue();
        d.Source.ShouldBe("skipped");
    }

    [Fact]
    public async Task BodyMarker_Matches_AllowsWithBodySource()
    {
        var body = $"<!-- polyphony:run_id={CurrentRunId} -->\nImpl PR body.";
        var d = await PrLineageGuard.CheckAsync(
            body, CurrentRunId, isManualLineage: false,
            bodyHasFrontMatter: false, NullJournal(),
            "pr_open_impl_pr",
            "branchpair:impl/1-2→mg/1_x", default);
        d.Allowed.ShouldBeTrue();
        d.Source.ShouldBe("body");
    }

    [Fact]
    public async Task BodyMarker_MismatchedRunId_RefusesWithBodySource()
    {
        // The exact W9 firewall case: another lineage opened this PR;
        // we must not merge it even if the journal happens to be silent.
        var body = $"<!-- polyphony:run_id={OtherRunId} -->\nImpl PR body.";
        var d = await PrLineageGuard.CheckAsync(
            body, CurrentRunId, isManualLineage: false,
            bodyHasFrontMatter: false, NullJournal(),
            "pr_open_impl_pr",
            "branchpair:impl/1-2→mg/1_x", default);
        d.Allowed.ShouldBeFalse();
        d.Source.ShouldBe("body");
        d.Reason.ShouldContain("foreign-lineage");
        d.Reason.ShouldContain(OtherRunId);
    }

    [Fact]
    public async Task PlanFrontMatter_Matches_AllowsWithBodySource()
    {
        var body =
            "---\nrequests_parent_change: false\nrun_id: " + CurrentRunId +
            "\nancestor_plan_generations: {}\n---\n\n## Plan body";
        var d = await PrLineageGuard.CheckAsync(
            body, CurrentRunId, isManualLineage: false,
            bodyHasFrontMatter: true, NullJournal(),
            "pr_open_plan_pr",
            "branchpair:plan/1-2→plan/1", default);
        d.Allowed.ShouldBeTrue();
        d.Source.ShouldBe("body");
    }

    [Fact]
    public async Task PlanFrontMatter_MismatchedRunId_Refuses()
    {
        var body =
            "---\nrequests_parent_change: false\nrun_id: " + OtherRunId +
            "\nancestor_plan_generations: {}\n---\n\n## Plan body";
        var d = await PrLineageGuard.CheckAsync(
            body, CurrentRunId, isManualLineage: false,
            bodyHasFrontMatter: true, NullJournal(),
            "pr_open_plan_pr",
            "branchpair:plan/1-2→plan/1", default);
        d.Allowed.ShouldBeFalse();
        d.Source.ShouldBe("body");
    }

    [Fact]
    public async Task NoBodyStamp_NullJournalStore_AllowsAsInconclusive()
    {
        // Without a journal AND without a stamp, we can't refuse without
        // false-positiving every PR that pre-dates the W6 rollout.
        // Default to allow so the W9 rollout doesn't brick existing
        // PRs the moment it ships.
        var d = await PrLineageGuard.CheckAsync(
            body: "no marker here, just a body.",
            CurrentRunId, isManualLineage: false,
            bodyHasFrontMatter: false, NullJournal(),
            "pr_open_impl_pr",
            "branchpair:impl/1-2→mg/1_x", default);
        d.Allowed.ShouldBeTrue();
        d.Source.ShouldBe("inconclusive");
    }
}
