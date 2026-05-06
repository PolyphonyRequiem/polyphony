using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Unit tests for <see cref="ManifestRebaseLedger.Apply"/>. The cascade-remedy
/// verb (Phase 3 P9 step 2) calls this on every successful rebase, including
/// idempotent replays after partial failures, so the duplicate-skip path
/// must hold tight.
/// </summary>
public sealed class ManifestRebaseLedgerTests
{
    private static readonly DateTime Ts = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Apply_FreshTriple_Appends()
    {
        var manifest = new RunManifest();
        var outcome = ManifestRebaseLedger.Apply(
            manifest, "plan/100-200", "deadbeef", "child_plan_drift", Ts);

        var appended = outcome.ShouldBeOfType<RebaseLedgerOutcome.Appended>();
        appended.Record.Branch.ShouldBe("plan/100-200");
        appended.Record.Commit.ShouldBe("deadbeef");
        appended.Record.Reason.ShouldBe("child_plan_drift");
        appended.Record.RecordedAt.ShouldBe(Ts);
        manifest.Rebases.Count.ShouldBe(1);
        manifest.Rebases[0].ShouldBeSameAs(appended.Record);
    }

    [Fact]
    public void Apply_DuplicateTriple_SkipsWithoutMutation()
    {
        var manifest = new RunManifest();
        ManifestRebaseLedger.Apply(manifest, "plan/100", "abc", "manual", Ts);
        var second = ManifestRebaseLedger.Apply(manifest, "plan/100", "abc", "manual", Ts.AddSeconds(5));

        var dup = second.ShouldBeOfType<RebaseLedgerOutcome.DuplicateSkipped>();
        // Existing entry is the original — not mutated by the second call.
        dup.Existing.RecordedAt.ShouldBe(Ts);
        manifest.Rebases.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_DistinctCommitsOnSameBranch_BothAppend()
    {
        var manifest = new RunManifest();
        ManifestRebaseLedger.Apply(manifest, "plan/100", "sha-a", "child_plan_drift", Ts);
        ManifestRebaseLedger.Apply(manifest, "plan/100", "sha-b", "child_plan_drift", Ts.AddMinutes(1));

        manifest.Rebases.Count.ShouldBe(2);
        manifest.Rebases[0].Commit.ShouldBe("sha-a");
        manifest.Rebases[1].Commit.ShouldBe("sha-b");
    }

    [Fact]
    public void Apply_SameCommitDifferentReason_BothAppend()
    {
        // (branch, commit, reason) is the key — distinct reason must NOT
        // count as a duplicate. The cascade-remedy verb relies on this when
        // the same SHA is reachable via multiple cascade paths.
        var manifest = new RunManifest();
        ManifestRebaseLedger.Apply(manifest, "plan/100", "sha", "child_plan_drift", Ts);
        ManifestRebaseLedger.Apply(manifest, "plan/100", "sha", "cross_mg_code_dep", Ts);

        manifest.Rebases.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("cross_mg_code_dep")]
    [InlineData("child_plan_drift")]
    [InlineData("manual")]
    public void Apply_AcceptsEachAllowedReason(string reason)
    {
        var manifest = new RunManifest();
        var outcome = ManifestRebaseLedger.Apply(manifest, "plan/100", "sha", reason, Ts);

        outcome.ShouldBeOfType<RebaseLedgerOutcome.Appended>();
        manifest.Rebases.Single().Reason.ShouldBe(reason);
    }

    [Fact]
    public void Apply_InvalidReason_ReturnsInvalidReason_WithAllowList()
    {
        var manifest = new RunManifest();
        var outcome = ManifestRebaseLedger.Apply(manifest, "plan/100", "sha", "made_up_reason", Ts);

        var invalid = outcome.ShouldBeOfType<RebaseLedgerOutcome.InvalidReason>();
        invalid.Provided.ShouldBe("made_up_reason");
        invalid.Allowed.ShouldBe(["cross_mg_code_dep", "child_plan_drift", "manual"]);
        manifest.Rebases.ShouldBeEmpty();
    }

    [Fact]
    public void Apply_RejectsNullManifest()
    {
        Should.Throw<ArgumentNullException>(() =>
            ManifestRebaseLedger.Apply(null!, "b", "c", "manual", Ts));
    }

    [Fact]
    public void Apply_RejectsEmptyBranchOrCommitOrReason()
    {
        var m = new RunManifest();
        Should.Throw<ArgumentException>(() => ManifestRebaseLedger.Apply(m, "", "c", "manual", Ts));
        Should.Throw<ArgumentException>(() => ManifestRebaseLedger.Apply(m, "b", "", "manual", Ts));
        Should.Throw<ArgumentException>(() => ManifestRebaseLedger.Apply(m, "b", "c", "", Ts));
    }

    [Fact]
    public void Apply_PreservesPriorEntriesWhenAppending()
    {
        var manifest = new RunManifest();
        manifest.Rebases.Add(new RebaseRecord
        {
            Branch = "plan/99",
            Onto = "feature/99",
            Reason = "manual",
            Commit = "preexisting",
            RecordedAt = Ts.AddDays(-1),
        });

        ManifestRebaseLedger.Apply(manifest, "plan/100", "freshsha", "child_plan_drift", Ts);

        manifest.Rebases.Count.ShouldBe(2);
        manifest.Rebases[0].Commit.ShouldBe("preexisting");
        manifest.Rebases[1].Commit.ShouldBe("freshsha");
    }
}
