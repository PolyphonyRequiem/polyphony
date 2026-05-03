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

    // --- Tag-aware: polyphony:planned (state-machine bypass for stuck Proposed) ---

    [Fact]
    public void Detect_EpicInProposedWithPlannedTagAndChildren_ReturnsReadyForImplementation()
    {
        // Regression: Epic was planned (children seeded, parent tagged), but
        // its state never transitioned out of "To Do". Without the tag check,
        // it would route back to NeedsPlanning forever.
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("To Do").WithTags("polyphony:planned").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child1, child2]);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_IssueInProposedWithPlannedTagNoChildren_ReturnsReadyForImplementation()
    {
        // Atomic case: Issue is plannable+implementable, architect emitted no
        // tasks (atomic-by-design), seeder set the tag. State stuck in "To Do".
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithType("Issue").WithState("To Do").WithTags("polyphony:planned").Build();

        var result = detector.Detect(item, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProposedWithPlannedTagNoChildren_ReturnsNeedsSeeding()
    {
        // Anomaly: plannable-only type (Epic) was tagged but has no children —
        // architect emitted [] for a type that requires decomposition. Surface
        // as NeedsSeeding so the bug is visible rather than silently advancing.
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithType("Epic").WithState("To Do").WithTags("polyphony:planned").Build();

        var result = detector.Detect(item, []);

        (result is NeedsSeeding).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInProposedWithoutPlannedTag_StillReturnsNeedsPlanning()
    {
        // Sanity check: the tag is required — an empty tags field does not
        // change the legacy NeedsPlanning behavior.
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("To Do").Build();
        var child = new WorkItemBuilder().WithId(2).WithType("Task").WithState("To Do").Build();

        var result = detector.Detect(item, [child]);

        (result is NeedsPlanning).ShouldBeTrue();
    }

    [Fact]
    public void Detect_PlannedTagMixedWithOtherTags_StillRecognized()
    {
        // System.Tags is a semicolon-separated string; the planned tag may be
        // surrounded by other tags (e.g. legacy "twig") with arbitrary spacing.
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithType("Issue").WithState("To Do")
            .WithTags("twig; polyphony:planned ; some-other-tag").Build();

        var result = detector.Detect(item, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_PlannedTagCaseInsensitive()
    {
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithType("Issue").WithState("To Do").WithTags("Polyphony:Planned").Build();

        var result = detector.Detect(item, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void Detect_PartialTagMatchDoesNotTrigger()
    {
        // "polyphony:planned-foo" must not match "polyphony:planned".
        var detector = CreateDetector();
        var item = new WorkItemBuilder()
            .WithType("Epic").WithState("To Do").WithTags("polyphony:planned-foo").Build();

        var result = detector.Detect(item, []);

        (result is NeedsPlanning).ShouldBeTrue();
    }

    [Fact]
    public void Detect_EpicInDoingWithPlannedTagAndChildren_BehavesIdenticallyToUntagged()
    {
        // Once state has transitioned to Doing, the tag is informational only —
        // existing children-classification logic dominates.
        var detector = CreateDetector();
        var taggedItem = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").WithTags("polyphony:planned").Build();
        var untaggedItem = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder().WithId(2).WithType("Task").WithState("Done").Build();
        var child2 = new WorkItemBuilder().WithId(3).WithType("Task").WithState("Done").Build();

        var taggedResult = detector.Detect(taggedItem, [child1, child2]);
        var untaggedResult = detector.Detect(untaggedItem, [child1, child2]);

        taggedResult.GetType().ShouldBe(untaggedResult.GetType());
        (taggedResult is ReadyForCompletion).ShouldBeTrue();
    }
}
