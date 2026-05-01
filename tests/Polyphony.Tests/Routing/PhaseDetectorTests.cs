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

        (result is NeedsPlanning).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProgressNoChildren_ReturnsNeedsSeeding()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        (result is NeedsSeeding).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProgressAllProposedChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child1, child2]);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProgressMixedChildren_ReturnsInProgress()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child1, child2]);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProgressAllCompleted_ReturnsReadyForCompletion()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(item, [child1, child2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicWithRemovedChildren_TreatsRemovedAsComplete()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("Removed").Build();

        var result = detector.Detect(item, [child1, child2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    // --- Implementable (Task) ---

    [Fact]
    public void Detect_TaskInProposed_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_TaskInProgress_ReturnsInProgress()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    // --- Plannable + Implementable (Issue) ---

    [Fact]
    public void Detect_IssueInProposed_ReturnsNeedsPlanning()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Issue").WithState("To Do").Build();

        var result = detector.Detect(item, []);

        (result is NeedsPlanning).ShouldBeTrue();
    }

    [Fact]
    public void Detect_IssueInProgressNoChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Issue").WithState("Doing").Build();

        var result = detector.Detect(item, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_IssueInProgressWithProposedChildren_ReturnsReadyForImplementation()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithState("Doing").Build();
        var child = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child]);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_IssueInProgressWithAllDoneChildren_ReturnsReadyForCompletion()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithId(1).WithType("Issue").WithState("Doing").Build();
        var child = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(item, [child]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    // --- Terminal states ---

    [Fact]
    public void Detect_CompletedItem_ReturnsDone()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("Done").Build();

        var result = detector.Detect(item, []);

        (result is RoutingDone).ShouldBeTrue();
    }

    [Fact]
    public void Detect_RemovedItem_ReturnsRemoved()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Task").WithState("Removed").Build();

        var result = detector.Detect(item, []);

        (result is RoutingRemoved).ShouldBeTrue();
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

        (result is RoutingUnknown).ShouldBeTrue();
    }

    // --- Message populated ---

    [Fact]
    public void Detect_AlwaysIncludesMessage()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder().WithType("Epic").WithState("To Do").Build();

        var result = detector.Detect(item, []);
        (result is NeedsPlanning).ShouldBeTrue();

        var message = result switch
        {
            NeedsPlanning d => d.Message,
            _ => null,
        };
        message.ShouldNotBeNullOrWhiteSpace();
    }
}
