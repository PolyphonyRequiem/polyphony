using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Journal.Reset;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class ResetRootProjectionCommandTests : CommandTestBase
{
    [Fact]
    public async Task ResetRoot_UsesProjectionExecutor()
    {
        var world = new MutableBranchWorld(exists: true, matchesExpected: true, actualState: "abc123");
        var observer = new MutableBranchObserver(world);
        var deleter = new MutableBranchDeleter(world);
        var executor = new ProjectionResetExecutor(
            new FakeJournalStore(
            [
                new JournalEntry
                {
                    Id = 1,
                    RunId = "run-1",
                    RootId = 100,
                    WorkItemId = 100,
                    Action = "branch_ensure_feature",
                    Target = "feature/100",
                    StartedAt = 1_000,
                    FinishedAt = 1_001,
                    Outcome = JournalOutcome.Success,
                    Effects =
                    [
                        new JournalResourceEffect
                        {
                            Kind = ResourceKind.GitBranch,
                            Id = "feature/100",
                            Intent = ResourceIntent.EnsurePresent,
                            Mutation = ResourceMutation.CreatedNow,
                            PolyphonyOwned = true,
                        },
                    ],
                },
            ]),
            new JournalDriftAnalyzer([observer]),
            new ProjectionResetCoverageAnalyzer([observer], [deleter]),
            new ProjectionResetPlanner(),
            [deleter]);
        var command = CreateCommand(executor);

        var (exit, output) = await CaptureConsoleAsync(() => command.ResetRoot(root: 100, execute: false));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetRootResult);

        exit.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Root.ShouldBe(100);
        result.DryRun.ShouldBeTrue();
        result.Success.ShouldBeTrue();
        result.AttemptedTargets.ShouldHaveSingleItem();
        result.AttemptedTargets[0].Kind.ShouldBe(ResourceKind.GitBranch);
        result.AttemptedTargets[0].Id.ShouldBe("feature/100");
        result.DeletedTargets.ShouldBeEmpty();
    }

    private ResetCommands CreateCommand(ProjectionResetExecutor executor)
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);
        return new ResetCommands(twig, git, pullRequestReader, planObserver, walker, projectionResetExecutor: executor);
    }

    private sealed class FakeJournalStore(IReadOnlyList<JournalEntry> entries) : IJournalStore
    {
        public string DatabasePath => "journal.db";
        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, IReadOnlyList<JournalResourceEffect>? effects, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct) => Task.FromResult(entries);
        public Task ExportAsync(string destinationPath, CancellationToken ct) => throw new NotSupportedException();
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

        public Task<ResourceDeleteOutcome> DeleteAsync(ProjectedResourceState resource, ObservedResourceState? observation, ResourceDeletionContext context, CancellationToken ct)
        {
            world.Exists = false;
            world.MatchesExpected = false;
            world.ActualState = "missing";
            return Task.FromResult(new ResourceDeleteOutcome { Success = true, Deleted = true });
        }
    }
}
