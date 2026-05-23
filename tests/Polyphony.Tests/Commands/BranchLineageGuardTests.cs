using Polyphony.Commands;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W10 (AB#3291): unit coverage for <see cref="BranchLineageGuard"/>.
/// Mirrors the matrix that <see cref="PrLineageGuard"/>'s tests use:
/// every "unknown" signal short-circuits to allow; only positive
/// foreign-ownership evidence refuses.
/// </summary>
public sealed class BranchLineageGuardTests
{
    [Fact]
    public async Task CheckAsync_NoRunId_Allows()
    {
        var decision = await BranchLineageGuard.CheckAsync(
            journal: new NullJournalStore(),
            currentRunId: null,
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("no run id");
    }

    [Fact]
    public async Task CheckAsync_ManualLineage_Allows()
    {
        var decision = await BranchLineageGuard.CheckAsync(
            journal: new NullJournalStore(),
            currentRunId: "01JCTEST",
            isManualLineage: true,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("manual lineage");
    }

    [Fact]
    public async Task CheckAsync_NullJournalStore_Allows()
    {
        var decision = await BranchLineageGuard.CheckAsync(
            journal: new NullJournalStore(),
            currentRunId: "01JCTEST",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("no journal store");
    }

    [Fact]
    public async Task CheckAsync_JournalSilent_Allows()
    {
        using var fixture = new JournalFixture();
        var decision = await BranchLineageGuard.CheckAsync(
            journal: fixture.Store,
            currentRunId: "01JCFRESH",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("Journal silent");
    }

    [Fact]
    public async Task CheckAsync_PriorRowFromCurrentRun_Allows()
    {
        using var fixture = new JournalFixture();
        await RecordEnsureAsync(fixture.Store, "01JCOURS", "impl/100-200");

        var decision = await BranchLineageGuard.CheckAsync(
            journal: fixture.Store,
            currentRunId: "01JCOURS",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("current run id");
    }

    [Fact]
    public async Task CheckAsync_PriorRowFromForeignRun_Refuses()
    {
        using var fixture = new JournalFixture();
        await RecordEnsureAsync(fixture.Store, "01JCFOREIGN", "impl/100-200");

        var decision = await BranchLineageGuard.CheckAsync(
            journal: fixture.Store,
            currentRunId: "01JCMINE",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("01JCFOREIGN");
        decision.Reason.ShouldContain("01JCMINE");
        decision.Reason.ShouldContain("foreign-lineage");
    }

    [Fact]
    public async Task CheckAsync_PriorRowFromBothCurrentAndForeign_Allows()
    {
        using var fixture = new JournalFixture();
        await RecordEnsureAsync(fixture.Store, "01JCFOREIGN", "impl/100-200");
        await RecordEnsureAsync(fixture.Store, "01JCMINE", "impl/100-200");

        var decision = await BranchLineageGuard.CheckAsync(
            journal: fixture.Store,
            currentRunId: "01JCMINE",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("current run id");
    }

    [Fact]
    public async Task CheckAsync_RowForDifferentTarget_DoesNotInterfere()
    {
        using var fixture = new JournalFixture();
        await RecordEnsureAsync(fixture.Store, "01JCFOREIGN", "impl/100-999");

        var decision = await BranchLineageGuard.CheckAsync(
            journal: fixture.Store,
            currentRunId: "01JCMINE",
            isManualLineage: false,
            journalAction: "branch_ensure_impl",
            journalTarget: "impl/100-200",
            CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("Journal silent");
    }

    private static async Task RecordEnsureAsync(JournalStore store, string runId, string target)
    {
        var actionId = await store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = 100,
                WorkItemId = 200,
                Action = "branch_ensure_impl",
                Target = target,
                StartedAt = 1_800_000_000_000,
            },
            CancellationToken.None);
        await store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, null, CancellationToken.None);
    }

    private sealed class JournalFixture : IDisposable
    {
        private readonly string _tempDir;
        public JournalStore Store { get; }

        public JournalFixture()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-branch-lineage-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            Store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
