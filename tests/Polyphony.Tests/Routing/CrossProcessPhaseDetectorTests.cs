using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;
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
        string[] MiddleFacets,
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
            Array.Exists(t.MiddleFacets, c => c == "implementable"),
        });

    private static CompositeTemplate GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private static PhaseDetector CreateDetector(CompositeTemplate template)
    {
        var config = new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name)
            .WithType(template.TopType, ["plannable"])
            .WithType(template.MiddleType, template.MiddleFacets)
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

        (result is NeedsPlanning).ShouldBeTrue();
    }

    // --- Scenario 2: Intermediate plannable routes based on facets ---

    [Theory]
    [MemberData(nameof(IntermediateNoChildrenData))]
    public void IntermediatePlannable_InProgress_NoChildren_RoutesBasedOnFacets(
        string templateName, bool isImplementable)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        var middle = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.InProgressState).Build();

        var result = detector.Detect(middle, []);

        if (isImplementable)
            (result is ReadyForImplementation).ShouldBeTrue();
        else
            (result is NeedsSeeding).ShouldBeTrue();
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

        (result is ReadyForImplementation).ShouldBeTrue();
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

        (result is ReadyForImplementation).ShouldBeTrue();
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

        (result is ReadyForCompletion).ShouldBeTrue();
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

        (result is ReadyForCompletion).ShouldBeTrue();
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

        (result is ImplementationInProgress).ShouldBeTrue();
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

        (result is ImplementationInProgress).ShouldBeTrue();
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

        (result is ReadyForImplementation).ShouldBeTrue();
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

        (result is NeedsSeeding).ShouldBeTrue();
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

        (result is ImplementationInProgress).ShouldBeTrue();
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

        (result is RoutingDone).ShouldBeTrue();
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

        (result is RoutingRemoved).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scrum-specific: Three InProgress state variants
    // (Approved, Committed, In Progress) all map to StateCategory.InProgress
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Approved")]
    [InlineData("Committed")]
    [InlineData("In Progress")]
    public void ScrumInProgressVariant_MapsToStateCategoryInProgress(string state)
    {
        var category = StateCategoryResolver.Resolve(state, entries: null);

        category.ShouldBe(StateCategory.InProgress);
    }

    // --- PBI (plannable+implementable) with Approved state ---

    [Fact]
    public void ScrumPBI_InApproved_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var pbi = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("Approved").Build();
        var task1 = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Done").Build();
        var task2 = new WorkItemBuilder().WithId(11).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(pbi, [task1, task2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void ScrumPBI_InApproved_NoChildren_ReturnsReadyForImplementation()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var pbi = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("Approved").Build();

        var result = detector.Detect(pbi, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void ScrumPBI_InApproved_MixedChildren_ReturnsInProgress()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var pbi = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("Approved").Build();
        var doneTask = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Done").Build();
        var newTask = new WorkItemBuilder().WithId(11).WithType("Task").WithState("New").Build();

        var result = detector.Detect(pbi, [doneTask, newTask]);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    // --- PBI with "In Progress" state (distinct from Committed/Approved) ---

    [Fact]
    public void ScrumPBI_InInProgress_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var pbi = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("In Progress").Build();
        var task1 = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Done").Build();
        var task2 = new WorkItemBuilder().WithId(11).WithType("Task").WithState("Done").Build();

        var result = detector.Detect(pbi, [task1, task2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void ScrumPBI_InInProgress_NoChildren_ReturnsReadyForImplementation()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var pbi = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("In Progress").Build();

        var result = detector.Detect(pbi, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    // --- Task (implementable-only) with Approved and "In Progress" states ---

    [Fact]
    public void ScrumTask_InApproved_ReturnsInProgress()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var task = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Approved").Build();

        var result = detector.Detect(task, []);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    [Fact]
    public void ScrumTask_InInProgress_ReturnsInProgress()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var task = new WorkItemBuilder().WithId(10).WithType("Task").WithState("In Progress").Build();

        var result = detector.Detect(task, []);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    // --- Epic (plannable-only) with Approved and "In Progress" states ---

    [Fact]
    public void ScrumEpic_InApproved_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Approved").Build();
        var pbi1 = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("Done").Build();
        var pbi2 = new WorkItemBuilder().WithId(3).WithType("Product Backlog Item").WithState("Done").Build();

        var result = detector.Detect(epic, [pbi1, pbi2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void ScrumEpic_InApproved_NoChildren_ReturnsNeedsSeeding()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Approved").Build();

        var result = detector.Detect(epic, []);

        (result is NeedsSeeding).ShouldBeTrue();
    }

    [Fact]
    public void ScrumEpic_InInProgress_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("In Progress").Build();
        var pbi1 = new WorkItemBuilder().WithId(2).WithType("Product Backlog Item").WithState("Done").Build();
        var pbi2 = new WorkItemBuilder().WithId(3).WithType("Product Backlog Item").WithState("Done").Build();

        var result = detector.Detect(epic, [pbi1, pbi2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void ScrumEpic_InInProgress_NoChildren_ReturnsNeedsSeeding()
    {
        var t = GetTemplate("Scrum");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("In Progress").Build();

        var result = detector.Detect(epic, []);

        (result is NeedsSeeding).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CMMI-specific: Resolved state mapping and routing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CmmiResolvedState_MapsToStateCategoryResolved()
    {
        // Explicit verification that the CMMI "Resolved" state name
        // resolves to StateCategory.Resolved via the fallback heuristic.
        var category = StateCategoryResolver.Resolve("Resolved", entries: null);

        category.ShouldBe(StateCategory.Resolved);
    }

    [Fact]
    public void CmmiRequirement_InResolved_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        // Key CMMI edge case: a plannable+implementable Requirement in the
        // Resolved state with all children complete should route to ReadyForCompletion.
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var requirement = new WorkItemBuilder().WithId(2).WithType("Requirement").WithState("Resolved").Build();
        var task1 = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Closed").Build();
        var task2 = new WorkItemBuilder().WithId(11).WithType("Task").WithState("Closed").Build();

        var result = detector.Detect(requirement, [task1, task2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void CmmiRequirement_InResolved_NoChildren_ReturnsReadyForImplementation()
    {
        // Plannable+implementable item in Resolved with no children
        // behaves like InProgress — routes to direct implementation.
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var requirement = new WorkItemBuilder().WithId(2).WithType("Requirement").WithState("Resolved").Build();

        var result = detector.Detect(requirement, []);

        (result is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void CmmiRequirement_InResolved_MixedChildren_ReturnsInProgress()
    {
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var requirement = new WorkItemBuilder().WithId(2).WithType("Requirement").WithState("Resolved").Build();
        var doneTask = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Closed").Build();
        var proposedTask = new WorkItemBuilder().WithId(11).WithType("Task").WithState("Proposed").Build();

        var result = detector.Detect(requirement, [doneTask, proposedTask]);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    [Fact]
    public void CmmiTask_InResolved_ReturnsInProgress()
    {
        // Implementable-only item in Resolved maps to InProgress/Monitor.
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var task = new WorkItemBuilder().WithId(10).WithType("Task").WithState("Resolved").Build();

        var result = detector.Detect(task, []);

        (result is ImplementationInProgress).ShouldBeTrue();
    }

    [Fact]
    public void CmmiEpic_InResolved_AllChildrenComplete_ReturnsReadyForCompletion()
    {
        // Plannable-only Epic in Resolved with all children complete → ReadyForCompletion.
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Resolved").Build();
        var req1 = new WorkItemBuilder().WithId(2).WithType("Requirement").WithState("Closed").Build();
        var req2 = new WorkItemBuilder().WithId(3).WithType("Requirement").WithState("Closed").Build();

        var result = detector.Detect(epic, [req1, req2]);

        (result is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void CmmiEpic_InResolved_NoChildren_ReturnsNeedsSeeding()
    {
        // Plannable-only Epic in Resolved with no children → NeedsSeeding.
        var t = GetTemplate("CMMI");
        var detector = CreateDetector(t);
        var epic = new WorkItemBuilder().WithId(1).WithType("Epic").WithState("Resolved").Build();

        var result = detector.Detect(epic, []);

        (result is NeedsSeeding).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unknown phase: type not registered in process config
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void UnregisteredType_ReturnsUnknown(string templateName)
    {
        var t = GetTemplate(templateName);
        var detector = CreateDetector(t);
        // "Bug" is not registered in any template config
        var item = new WorkItemBuilder().WithId(99).WithType("Bug").WithState(t.InProgressState).Build();

        var result = detector.Detect(item, []);

        (result is RoutingUnknown).ShouldBeTrue();
    }
}


