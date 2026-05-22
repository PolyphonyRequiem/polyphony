using System.Text.Json.Nodes;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal.Projections;

public sealed class JournalDriftProjectionTests
{
    [Fact]
    public void CurrentExpectedState_Project_IgnoresFailedEntries_AndKeepsLatestTerminalEffect()
    {
        var entries = new[]
        {
            Entry(
                id: 1,
                startedAt: 1_000,
                outcome: JournalOutcome.Success,
                action: "branch_ensure_feature",
                target: "feature/3268",
                Effect(ResourceKind.GitBranch, "feature/3268", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, attributes: new JsonObject { ["new_sha"] = "abc123" })),
            Entry(
                id: 2,
                startedAt: 2_000,
                outcome: JournalOutcome.Failure,
                action: "branch_delete_feature",
                target: "feature/3268",
                Effect(ResourceKind.GitBranch, "feature/3268", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow)),
            Entry(
                id: 3,
                startedAt: 3_000,
                outcome: JournalOutcome.NoOp,
                action: "branch_ensure_feature",
                target: "feature/3268",
                Effect(ResourceKind.GitBranch, "feature/3268", ResourceIntent.EnsurePresent, ResourceMutation.NoChangedAlreadySatisfied, attributes: new JsonObject { ["new_sha"] = "def456" })),
        };

        var result = CurrentExpectedState.Project(entries);

        result.Resources.ShouldHaveSingleItem();
        var resource = result.Resources[0];
        resource.EntryId.ShouldBe(3);
        resource.StartedAt.ShouldBe(3_000);
        resource.Intent.ShouldBe(ResourceIntent.EnsurePresent);
        resource.Mutation.ShouldBe(ResourceMutation.NoChangedAlreadySatisfied);
        resource.Attributes.ShouldNotBeNull();
        resource.Attributes!["new_sha"]!.ToString().ShouldBe("def456");
    }

    [Fact]
    public void ResetTargets_Project_ReturnsOnlyOwnedPresentResourcesThatShouldExist()
    {
        var currentExpectedState = CurrentExpectedState.Project(
        [
            Effect(ResourceKind.GitBranch, "feature/3268", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true),
            Effect(ResourceKind.GitTag, "v3268", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, polyphonyOwned: true),
            Effect(ResourceKind.GitHubPr, "https://github.com/owner/repo/pull/42", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: false),
        ]);

        var result = ResetTargets.Project(
            currentExpectedState,
            [
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3268",
                    Exists = true,
                    MatchesExpectedState = true,
                    ActualState = "abc123",
                },
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitTag,
                    Id = "v3268",
                    Exists = true,
                    MatchesExpectedState = false,
                    ActualState = "present",
                },
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitHubPr,
                    Id = "https://github.com/owner/repo/pull/42",
                    Exists = true,
                    MatchesExpectedState = true,
                    ActualState = "open",
                },
            ]);

        result.Resources.ShouldHaveSingleItem();
        result.Resources[0].Kind.ShouldBe(ResourceKind.GitBranch);
        result.Resources[0].Id.ShouldBe("feature/3268");
    }

    [Fact]
    public async Task JournalDriftAnalyzer_ProducesDeleteMutationAndCreateFindings()
    {
        var entries = new[]
        {
            Entry(
                id: 1,
                startedAt: 1_000,
                outcome: JournalOutcome.Success,
                action: "branch_ensure_feature",
                target: "feature/3268",
                Effect(ResourceKind.GitBranch, "feature/3268", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, attributes: new JsonObject { ["new_sha"] = "abc123" })),
            Entry(
                id: 2,
                startedAt: 1_100,
                outcome: JournalOutcome.Success,
                action: "workitem_read",
                target: "workitem:3268",
                Effect(ResourceKind.AdoWorkItem, "workitem:3268", ResourceIntent.EnsurePresent, ResourceMutation.NoChangedAlreadySatisfied)),
        };

        var analyzer = new JournalDriftAnalyzer(
        [
            new FakeResourceObserver(
                new ResourceObservationBatch
                {
                    Kind = ResourceKind.GitBranch,
                    Observations =
                    [
                        new ObservedResourceState
                        {
                            Kind = ResourceKind.GitBranch,
                            Id = "feature/3268",
                            Exists = true,
                            MatchesExpectedState = false,
                            ActualState = "def456",
                        },
                    ],
                    DiscoveredResources =
                    [
                        new DiscoveredResourceState
                        {
                            Kind = ResourceKind.GitBranch,
                            Id = "feature/3268-shadow",
                            MatchesPolyphonyPattern = true,
                            ActualState = "present",
                        },
                    ],
                }),
            new FakeResourceObserver(
                new ResourceObservationBatch
                {
                    Kind = ResourceKind.AdoWorkItem,
                    Observations =
                    [
                        new ObservedResourceState
                        {
                            Kind = ResourceKind.AdoWorkItem,
                            Id = "workitem:3268",
                            Exists = false,
                            MatchesExpectedState = false,
                            ActualState = "missing",
                        },
                    ],
                    DiscoveredResources = [],
                }),
        ]);

        var analysis = await analyzer.AnalyzeAsync(3268, entries, CancellationToken.None);

        analysis.Result.Status.ShouldBe("ok");
        analysis.Result.RootId.ShouldBe(3268);
        analysis.Result.Findings.Length.ShouldBe(3);
        analysis.Result.Findings.Single(finding => finding.Classification == DriftClassifications.ExternalDelete).Id.ShouldBe("workitem:3268");
        analysis.Result.Findings.Single(finding => finding.Classification == DriftClassifications.ExternalMutation).Id.ShouldBe("feature/3268");
        analysis.Result.Findings.Single(finding => finding.Classification == DriftClassifications.ExternalCreatePolyphonyNamed).Id.ShouldBe("feature/3268-shadow");
        analysis.Result.Summary.Consistent.ShouldBe(0);
        analysis.Result.Summary.ExternalDelete.ShouldBe(1);
        analysis.Result.Summary.ExternalMutation.ShouldBe(1);
        analysis.Result.Summary.ExternalCreate.ShouldBe(1);
        analysis.ResetTargets.Resources.ShouldHaveSingleItem();
        analysis.ResetTargets.Resources[0].Id.ShouldBe("feature/3268");
    }

    private static JournalEntry Entry(long id, long startedAt, JournalOutcome outcome, string action, string target, params JournalResourceEffect[] effects)
        => new()
        {
            Id = id,
            RunId = $"run-{id}",
            RootId = 3268,
            WorkItemId = 3268,
            Action = action,
            Target = target,
            StartedAt = startedAt,
            FinishedAt = startedAt + 1,
            Outcome = outcome,
            Effects = effects,
        };

    private static JournalResourceEffect Effect(
        string kind,
        string id,
        ResourceIntent intent,
        ResourceMutation mutation,
        bool polyphonyOwned = true,
        JsonObject? attributes = null)
        => new()
        {
            Kind = kind,
            Id = id,
            Intent = intent,
            Mutation = mutation,
            PolyphonyOwned = polyphonyOwned,
            Attributes = attributes,
        };

    private sealed class FakeResourceObserver(ResourceObservationBatch batch) : IResourceObserver
    {
        public string Kind => batch.Kind;
        public bool CanObserve => true;
        public string? DeferredReason => null;

        public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
        {
            request.RootId.ShouldBe(3268);
            request.ExpectedResources.ShouldNotBeEmpty();
            request.ExpectedResources.Select(resource => resource.Kind).Distinct().ShouldBe([Kind]);
            return Task.FromResult(batch);
        }
    }
}
