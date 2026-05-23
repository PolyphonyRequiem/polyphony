using System.Text.Json.Nodes;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Journal.Reset;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal.Reset;

public sealed class ProjectionResetExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_IncompleteCoverageWithoutAllowUnjournaled_FailsCoverageGate()
    {
        JournalEntry[] entries =
        [
            Entry(id: 1, startedAt: 1_000, action: "fixture_no_effects"),
        ];
        var executor = new ProjectionResetExecutor(
            new FakeJournalStore(entries),
            new JournalDriftAnalyzer([]),
            new ProjectionResetCoverageAnalyzer([], []),
            new ProjectionResetPlanner(),
            []);

        var result = await executor.ExecuteAsync(100, new ProjectionResetExecutionOptions { Execute = true }, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Strategy.ShouldBe(ProjectionResetStrategy.Projection);
        result.Coverage.ShouldBe(ProjectionResetCoverage.Incomplete);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("missing journal effects for actions: fixture_no_effects");
        result.DeletedTargets.ShouldBeEmpty();
        result.StepsCompleted.ShouldBeEmpty();
        result.StepsFailed.ShouldBe(["prs", "worktrees", "branches", "facets", "manifest", "state"]);
    }

    [Fact]
    public async Task ExecuteAsync_MutatedTargetWithoutForce_BlocksDeletion()
    {
        var world = new MutableBranchWorld(exists: true, matchesExpected: false, actualState: "def456");
        var observer = new MutableBranchObserver(world);
        var deleter = new MutableBranchDeleter(world);
        JournalEntry[] entries =
        [
            Entry(
                id: 1,
                startedAt: 1_000,
                action: "branch_ensure_feature",
                effects:
                [
                    Effect(ResourceKind.GitBranch, "feature/100", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow),
                ]),
        ];
        var executor = new ProjectionResetExecutor(
            new FakeJournalStore(entries),
            new JournalDriftAnalyzer([observer]),
            new ProjectionResetCoverageAnalyzer([observer], [deleter]),
            new ProjectionResetPlanner(),
            [deleter]);

        var result = await executor.ExecuteAsync(100, new ProjectionResetExecutionOptions { Execute = true }, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.BlockedMutatedTargets.ShouldHaveSingleItem();
        result.BlockedMutatedTargets[0].Kind.ShouldBe(ResourceKind.GitBranch);
        result.BlockedMutatedTargets[0].Id.ShouldBe("feature/100");
        result.DeletedTargets.ShouldBeEmpty();
        deleter.DeleteCount.ShouldBe(0);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("externally mutated");
    }

    [Fact]
    public async Task ExecuteAsync_ExecuteMode_ReobservesAndReportsSuccessfulDeletion()
    {
        var world = new MutableBranchWorld(exists: true, matchesExpected: true, actualState: "abc123");
        var observer = new MutableBranchObserver(world);
        var deleter = new MutableBranchDeleter(world);
        JournalEntry[] entries =
        [
            Entry(
                id: 1,
                startedAt: 1_000,
                action: "branch_ensure_feature",
                effects:
                [
                    Effect(ResourceKind.GitBranch, "feature/100", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow),
                ]),
        ];
        var executor = new ProjectionResetExecutor(
            new FakeJournalStore(entries),
            new JournalDriftAnalyzer([observer]),
            new ProjectionResetCoverageAnalyzer([observer], [deleter]),
            new ProjectionResetPlanner(),
            [deleter]);

        var result = await executor.ExecuteAsync(100, new ProjectionResetExecutionOptions { Execute = true }, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.DeletedTargets.ShouldHaveSingleItem();
        result.DeletedTargets[0].Kind.ShouldBe(ResourceKind.GitBranch);
        result.DeletedTargets[0].Id.ShouldBe("feature/100");
        result.RemainingResetTargets.ShouldBeEmpty();
        deleter.DeleteCount.ShouldBe(1);
        world.Exists.ShouldBeFalse();
    }

    private static JournalEntry Entry(long id, long startedAt, string action, IReadOnlyList<JournalResourceEffect>? effects = null)
        => new()
        {
            Id = id,
            RunId = $"run-{id}",
            RootId = 100,
            WorkItemId = 100,
            Action = action,
            Target = "root:100",
            StartedAt = startedAt,
            FinishedAt = startedAt + 1,
            Outcome = JournalOutcome.Success,
            Effects = effects ?? [],
        };

    private static JournalResourceEffect Effect(
        string kind,
        string id,
        ResourceIntent intent,
        ResourceMutation mutation,
        JsonObject? attributes = null)
        => new()
        {
            Kind = kind,
            Id = id,
            Intent = intent,
            Mutation = mutation,
            PolyphonyOwned = true,
            Attributes = attributes,
        };

    private sealed class FakeJournalStore(IReadOnlyList<JournalEntry> entries) : IJournalStore
    {
        public string DatabasePath => "journal.db";
        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, IReadOnlyList<JournalResourceEffect>? effects, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct) => Task.FromResult(entries);
        public Task ExportAsync(string destinationPath, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct) => Task.FromResult<IReadOnlyList<JournalLineage>>([]);
        public Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class MutableBranchWorld(bool exists, bool matchesExpected, string actualState)
    {
        public bool Exists { get; set; } = exists;
        public bool MatchesExpected { get; set; } = matchesExpected;
        public string ActualState { get; set; } = actualState;
    }

    private sealed class MutableBranchObserver(MutableBranchWorld world) : IResourceObserver
    {
        public string Kind => ResourceKind.GitBranch;
        public bool CanObserve => true;
        public string? DeferredReason => null;

        public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
            => Task.FromResult(new ResourceObservationBatch
            {
                Kind = Kind,
                Observations =
                [
                    new ObservedResourceState
                    {
                        Kind = Kind,
                        Id = "feature/100",
                        Exists = world.Exists,
                        MatchesExpectedState = world.Exists && world.MatchesExpected,
                        ActualState = world.Exists ? world.ActualState : "missing",
                    },
                ],
                DiscoveredResources = [],
            });
    }

    private sealed class MutableBranchDeleter(MutableBranchWorld world) : IResourceDeleter
    {
        public string Kind => ResourceKind.GitBranch;
        public int DeleteCount { get; private set; }

        public Task<ResourceDeleteOutcome> DeleteAsync(ProjectedResourceState resource, ObservedResourceState? observation, ResourceDeletionContext context, CancellationToken ct)
        {
            DeleteCount++;
            world.Exists = false;
            world.MatchesExpected = false;
            world.ActualState = "missing";
            return Task.FromResult(new ResourceDeleteOutcome { Success = true, Deleted = true });
        }
    }
}
