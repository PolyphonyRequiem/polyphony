using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Cross-process template tests for composite plannable→plannable→implementable hierarchy chains.
/// Validates that phase detection routes correctly at each level of a three-tier hierarchy
/// across Basic, Agile, Scrum, and CMMI process templates.
/// </summary>
public sealed class CrossProcessPhaseDetectorTests
{
    /// <summary>
    /// Defines the composite hierarchy structure for a single process template.
    /// </summary>
    private sealed record CompositeTemplate(
        string Name,
        string TopType,
        string MiddleType,
        string[] MiddleCapabilities,
        string LeafType,
        string ProposedState,
        string InProgressState,
        string CompletedState);

    private static readonly CompositeTemplate[] Templates =
    [
        new("Basic", "Epic", "Issue", ["plannable"], "Task", "To Do", "Doing", "Done"),
        new("Agile", "Epic", "User Story", ["plannable", "implementable"], "Task", "New", "Active", "Closed"),
        new("Scrum", "Epic", "Product Backlog Item", ["plannable", "implementable"], "Task", "New", "Committed", "Done"),
        new("CMMI", "Epic", "Requirement", ["plannable", "implementable"], "Task", "Proposed", "Active", "Closed"),
    ];

    public static IEnumerable<object[]> AllTemplateNames =>
        Templates.Select(t => new object[] { t.Name });

    public static IEnumerable<object[]> IntermediateNoChildrenData =>
        Templates.Select(t => new object[]
        {
            t.Name,
            Array.Exists(t.MiddleCapabilities, c => c == "implementable")
                ? SdlcPhase.ReadyForImplementation
                : SdlcPhase.NeedsSeeding,
            Array.Exists(t.MiddleCapabilities, c => c == "implementable")
                ? SdlcAction.Implement
                : SdlcAction.Seed,
        });

    private static CompositeTemplate GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private static PhaseDetector CreateDetector(CompositeTemplate template)
    {
        var config = new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name)
            .WithType(template.TopType, ["plannable"])
            .WithType(template.MiddleType, template.MiddleCapabilities)
            .WithType(template.LeafType, ["implementable"])
            .Build();
        return new PhaseDetector(config);
    }

    // --- Scenario 1: Top-level plannable in Proposed → NeedsPlanning ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void TopLevel_InProposed_WithUnsortedChildren_ReturnsNeedsPlanning(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.ProposedState).Build();
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.ProposedState).Build();

        var result = detector.Detect(epic, [middle]);

        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    // --- Scenario 2: Intermediate plannable routes based on capabilities ---

    [Theory]
    [MemberData(nameof(IntermediateNoChildrenData))]
    public void IntermediatePlannable_InProgress_NoChildren_RoutesBasedOnCapabilities(
        string templateName, string expectedPhase, string expectedAction)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.InProgressState).Build();

        var result = detector.Detect(middle, []);

        result.Phase.ShouldBe(expectedPhase);
        result.Action.ShouldBe(expectedAction);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void IntermediatePlannable_AllLeafChildrenProposed_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.InProgressState).Build();
        var leaf1 = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.ProposedState).Build();
        var leaf2 = new WorkItemBuilder().WithId(11).WithType(t.LeafType).WithState(t.ProposedState).Build();

        var result = detector.Detect(middle, [leaf1, leaf2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    // --- Scenario 3: Implementable leaf routes based on its own state ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void ImplementableLeaf_InProposed_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var leaf = new WorkItemBuilder().WithId(3).WithType(t.LeafType).WithState(t.ProposedState).Build();

        var result = detector.Detect(leaf, []);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    // --- Scenario 4: All children complete at each level → ReadyForCompletion ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void TopLevel_AllChildrenCompleted_ReturnsReadyForCompletion(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();
        var middle1 = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.CompletedState).Build();
        var middle2 = new WorkItemBuilder().WithId(3).WithType(t.MiddleType).WithState(t.CompletedState).Build();

        var result = detector.Detect(epic, [middle1, middle2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void IntermediatePlannable_AllLeafChildrenCompleted_ReturnsReadyForCompletion(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.InProgressState).Build();
        var leaf1 = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.CompletedState).Build();
        var leaf2 = new WorkItemBuilder().WithId(11).WithType(t.LeafType).WithState(t.CompletedState).Build();

        var result = detector.Detect(middle, [leaf1, leaf2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    // --- Scenario 5: Mixed children states → InProgress ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void TopLevel_MixedChildrenStates_ReturnsInProgress(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();
        var completedMiddle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.CompletedState).Build();
        var proposedMiddle = new WorkItemBuilder().WithId(3).WithType(t.MiddleType).WithState(t.ProposedState).Build();

        var result = detector.Detect(epic, [completedMiddle, proposedMiddle]);

        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void IntermediatePlannable_MixedLeafStates_ReturnsInProgress(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.InProgressState).Build();
        var doneLeaf = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.CompletedState).Build();
        var todoLeaf = new WorkItemBuilder().WithId(11).WithType(t.LeafType).WithState(t.ProposedState).Build();

        var result = detector.Detect(middle, [doneLeaf, todoLeaf]);

        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    // --- Additional: Top-level with all proposed children → ReadyForImplementation ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void TopLevel_AllChildrenProposed_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();
        var middle1 = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.ProposedState).Build();
        var middle2 = new WorkItemBuilder().WithId(3).WithType(t.MiddleType).WithState(t.ProposedState).Build();

        var result = detector.Detect(epic, [middle1, middle2]);

        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    // --- Scenario 6: Top-level plannable-only in InProgress with no children → NeedsSeeding ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void TopLevel_InProgress_NoChildren_ReturnsNeedsSeeding(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();

        var result = detector.Detect(epic, []);

        result.Phase.ShouldBe(SdlcPhase.NeedsSeeding);
        result.Action.ShouldBe(SdlcAction.Seed);
    }

    // --- Scenario 7: Implementable leaf in InProgress → InProgress ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void ImplementableLeaf_InProgress_ReturnsInProgress(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var leaf = new WorkItemBuilder().WithId(3).WithType(t.LeafType).WithState(t.InProgressState).Build();

        var result = detector.Detect(leaf, []);

        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    // --- Scenario 8: Terminal state — Completed → Done ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void AnyItem_InCompleted_ReturnsDone(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.CompletedState).Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.Done);
        result.Action.ShouldBe(SdlcAction.None);
    }

    // --- Scenario 9: Terminal state — Removed ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void AnyItem_InRemoved_ReturnsRemoved(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.LeafType).WithState("Removed").Build();

        var result = detector.Detect(item, []);

        result.Phase.ShouldBe(SdlcPhase.Removed);
        result.Action.ShouldBe(SdlcAction.None);
    }
}
