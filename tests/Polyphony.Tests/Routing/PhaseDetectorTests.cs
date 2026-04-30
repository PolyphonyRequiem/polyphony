using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Unit tests for <see cref="PhaseDetector"/> covering all paths in the phase detection rules table.
/// </summary>
public sealed class PhaseDetectorTests
{
    private static PhaseDetector CreateDetector(ProcessConfigBuilder? configBuilder = null)
    {
        var config = (configBuilder ?? DefaultConfigBuilder()).Build();
        return new PhaseDetector(config);
    }

    private static ProcessConfigBuilder DefaultConfigBuilder()
    {
        return new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done"
            })
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done"
            })
            .WithType("Task", ["implementable"], new Dictionary<string, string>
            {
                ["begin_implementation"] = "Doing",
                ["implementation_complete"] = "Done"
            });
    }

    // --- Plannable (Epic) ---

    [Fact]
    public void Detect_EpicInProposed_ReturnsNeedsPlanning()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    [Fact]
    public void Detect_EpicInProgressNoChildren_ReturnsNeedsSeeding()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.NeedsSeeding);
        result.Action.ShouldBe(SdlcAction.Seed);
    }

    [Fact]
    public void Detect_EpicInProgressAllProposedChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child1, child2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public void Detect_EpicInProgressMixedChildren_ReturnsInProgress()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child1, child2]);

        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    [Fact]
    public void Detect_EpicInProgressAllCompleted_ReturnsReadyForCompletion()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(item, [child1, child2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    [Fact]
    public void Detect_EpicWithRemovedChildren_TreatsRemovedAsComplete()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("Removed").Build();

        var result = detector.Detect(item, [child1, child2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    // --- Implementable (Task) ---

    [Fact]
    public void Detect_TaskInProposed_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public void Detect_TaskInProgress_ReturnsInProgress()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    // --- Plannable + Implementable (Issue) ---

    [Fact]
    public void Detect_IssueInProposed_ReturnsNeedsPlanning()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Issue").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    [Fact]
    public void Detect_IssueInProgressNoChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Issue").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public void Detect_IssueInProgressWithProposedChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithState("Doing").Build();
        var child = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public void Detect_IssueInProgressWithAllDoneChildren_ReturnsReadyForCompletion()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithState("Doing").Build();
        var child = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(item, [child]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    // --- Terminal states ---

    [Fact]
    public void Detect_CompletedItem_ReturnsDone()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("Done").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.Done);
        result.Action.ShouldBe(SdlcAction.None);
    }

    [Fact]
    public void Detect_RemovedItem_ReturnsRemoved()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("Removed").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.Removed);
        result.Action.ShouldBe(SdlcAction.None);
    }

    // --- Unknown type ---

    [Fact]
    public void Detect_UnknownType_ReturnsUnknown()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .Build();
        var detector = new PhaseDetector(config);

        // Use Task type but don't register it in config
        var item = new WorkItemBuilder().WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.Unknown);
        result.Action.ShouldBe(SdlcAction.None);
    }

    // --- Message populated ---

    [Fact]
    public void Detect_AlwaysIncludesMessage()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        result.Message.ShouldNotBeNullOrWhiteSpace();
    }
}
