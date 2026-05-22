using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class JournalDriftCommandTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public JournalDriftCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task Drift_RootNotFound_ReturnsCacheErrorWithCanonicalErrorJson()
    {
        var command = CreateCommand();

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Drift(99_991));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_991);
    }

    [Fact]
    public async Task Drift_JsonRender_ReturnsSerializedDriftResult()
    {
        await SeedAsync(new WorkItemBuilder().WithId(3268).WithType("Epic").WithTitle("Root").Build());
        await SeedEntryAsync(
            rootId: 3268,
            workItemId: 3268,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3268",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ]);

        var command = CreateCommand(
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
                    DiscoveredResources = [],
                }));

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Drift(3268));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.DriftResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Status.ShouldBe("ok");
        result.RootId.ShouldBe(3268);
        result.Findings.ShouldHaveSingleItem();
        result.Findings[0].Classification.ShouldBe(DriftClassifications.ExternalMutation);
        result.Summary.ExternalMutation.ShouldBe(1);
        result.ResetTargets.ShouldHaveSingleItem();
        result.ResetTargets[0].Kind.ShouldBe(ResourceKind.GitBranch);
        result.ResetTargets[0].Id.ShouldBe("feature/3268");
    }

    [Fact]
    public async Task Drift_TextRender_OutputsTabularFindings()
    {
        await SeedAsync(new WorkItemBuilder().WithId(3269).WithType("Epic").WithTitle("Root").Build());
        await SeedEntryAsync(
            rootId: 3269,
            workItemId: 3269,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.AdoWorkItem,
                    Id = "workitem:3269",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.NoChangedAlreadySatisfied,
                    PolyphonyOwned = true,
                },
            ]);

        var command = CreateCommand(
            new FakeResourceObserver(
                new ResourceObservationBatch
                {
                    Kind = ResourceKind.AdoWorkItem,
                    Observations =
                    [
                        new ObservedResourceState
                        {
                            Kind = ResourceKind.AdoWorkItem,
                            Id = "workitem:3269",
                            Exists = false,
                            MatchesExpectedState = false,
                            ActualState = "missing",
                        },
                    ],
                    DiscoveredResources = [],
                }));

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Drift(3269, render: "text"));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("root\t3269");
        output.ShouldContain("summary\tconsistent=0\texternal_delete=1\texternal_mutation=0\texternal_create=0");
        output.ShouldContain("classification\tkind\tid\texpected\tactual\towned");
        output.ShouldContain("external_delete\tado_work_item\tworkitem:3269\tpresent\tmissing\tTrue");
    }

    public override void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private JournalDriftCommand CreateCommand(params IResourceObserver[] observers)
        => new(_store, Repository, new JournalDriftAnalyzer(observers));

    private async Task SeedEntryAsync(int rootId, int workItemId, IReadOnlyList<JournalResourceEffect>? effects = null)
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "run-drift",
                RootId = rootId,
                WorkItemId = workItemId,
                Action = "journal_drift_fixture",
                Target = $"root:{rootId}",
                StartedAt = 1_700_000_000_000,
            },
            CancellationToken.None);

        await _store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, effects, CancellationToken.None);
    }

    private sealed class FakeResourceObserver(ResourceObservationBatch batch) : IResourceObserver
    {
        public string Kind => batch.Kind;
        public bool CanObserve => true;
        public string? DeferredReason => null;

        public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
        {
            request.ExpectedResources.ShouldNotBeEmpty();
            request.ExpectedResources.Select(resource => resource.Kind).Distinct().ShouldBe([Kind]);
            return Task.FromResult(batch);
        }
    }
}
