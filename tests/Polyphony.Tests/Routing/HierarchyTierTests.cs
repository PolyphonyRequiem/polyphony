using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for HierarchyWalker across 2-tier (Issue→Task), 3-tier (Epic→Middle→Task),
/// and 4-tier (Epic→Feature→Issue→Task) hierarchies.
/// Verifies that facet annotations are correct at every level for each process template.
/// </summary>
public sealed class HierarchyTierTests
{
    private readonly IWorkItemRepository _repository = Substitute.For<IWorkItemRepository>();

    // --- Template definitions for parameterized 3-tier tests ---

    /// <summary>
    /// Defines the 3-tier hierarchy structure for a single process template.
    /// </summary>
    private sealed record TemplateDefinition(
        string Name,
        string TopType,
        string[] TopFacets,
        string MiddleType,
        string[] MiddleFacets,
        string LeafType,
        string[] LeafFacets);

    private static readonly TemplateDefinition[] Templates =
    [
        new("Basic", "Epic", ["plannable"], "Issue", ["plannable", "implementable"], "Task", ["implementable"]),
        new("Agile", "Epic", ["plannable"], "User Story", ["plannable", "implementable"], "Task", ["implementable"]),
        new("Scrum", "Epic", ["plannable"], "Product Backlog Item", ["plannable", "implementable"], "Task", ["implementable"]),
        new("CMMI", "Epic", ["plannable"], "Requirement", ["plannable", "implementable"], "Task", ["implementable"]),
    ];

    public static IEnumerable<object[]> AllTemplateNames =>
        Templates.Select(t => new object[] { t.Name });

    private static TemplateDefinition GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private static ProcessConfig BuildConfig(TemplateDefinition template) =>
        new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name)
            .WithType(template.TopType, template.TopFacets)
            .WithType(template.MiddleType, template.MiddleFacets)
            .WithType(template.LeafType, template.LeafFacets)
            .Build();

    private HierarchyWalker CreateWalker(ProcessConfig config) => new(config, _repository);

    // ===================================================================
    // 2-tier: Issue → Task (Basic config, simplest hierarchy)
    // ===================================================================

    private static readonly ProcessConfig TwoTierConfig = new ProcessConfigBuilder()
        .WithProcessTemplate("Basic")
        .WithType("Issue", ["plannable", "implementable"])
        .WithType("Task", ["implementable"])
        .Build();

    [Fact]
    public async Task TwoTier_IssueToTask_WalksCorrectly()
    {
        var issue = new WorkItemBuilder()
            .WithId(1).WithType("Issue").WithTitle("Parent Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Child Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(issue);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(TwoTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1);
        result.Type.ShouldBe("Issue");
        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(1);
        result.Children[0].WorkItemId.ShouldBe(10);
        result.Children[0].Type.ShouldBe("Task");
    }

    [Fact]
    public async Task TwoTier_IssueToTask_AnnotatesFacetsCorrectly()
    {
        var issue = new WorkItemBuilder()
            .WithId(1).WithType("Issue").WithTitle("Parent Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Child Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(issue);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(TwoTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Facets.ShouldBe(["plannable", "implementable"]);
        result.Children.ShouldNotBeNull();
        result.Children[0].Facets.ShouldBe(["implementable"]);
    }

    [Fact]
    public async Task TwoTier_IssueWithMultipleTasks_AllChildrenAnnotated()
    {
        var issue = new WorkItemBuilder()
            .WithId(1).WithType("Issue").WithTitle("Parent Issue").WithState("Doing").Build();
        var task1 = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Task One").WithState("To Do").Build();
        var task2 = new WorkItemBuilder()
            .WithId(11).WithType("Task").WithTitle("Task Two").WithState("Doing").Build();
        var task3 = new WorkItemBuilder()
            .WithId(12).WithType("Task").WithTitle("Task Three").WithState("Done").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(issue);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task1, task2, task3 });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());
        _repository.GetChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());
        _repository.GetChildrenAsync(12, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(TwoTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(3);

        foreach (var child in result.Children)
        {
            child.Facets.ShouldBe(["implementable"]);
        }
    }

    [Fact]
    public async Task TwoTier_LeafTaskHasNoChildren()
    {
        var issue = new WorkItemBuilder()
            .WithId(1).WithType("Issue").WithTitle("Parent Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Child Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(issue);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(TwoTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldBeNull();
    }

    // ===================================================================
    // 3-tier: Epic → Middle → Task (parameterized across all 4 templates)
    // ===================================================================

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task ThreeTier_WalksFullHierarchy(string templateName)
    {
        var t = GetTemplate(templateName);
        var config = BuildConfig(t);

        var epic = new WorkItemBuilder()
            .WithId(1).WithType(t.TopType).WithTitle("Root").WithState("Doing").Build();
        var middle = new WorkItemBuilder()
            .WithId(10).WithType(t.MiddleType).WithTitle("Middle").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType(t.LeafType).WithTitle("Leaf").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { middle });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(config);
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1);
        result.Type.ShouldBe(t.TopType);

        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(1);
        result.Children[0].WorkItemId.ShouldBe(10);
        result.Children[0].Type.ShouldBe(t.MiddleType);

        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children!.Length.ShouldBe(1);
        result.Children[0].Children![0].WorkItemId.ShouldBe(100);
        result.Children[0].Children![0].Type.ShouldBe(t.LeafType);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task ThreeTier_AnnotatesFacetsAtEveryLevel(string templateName)
    {
        var t = GetTemplate(templateName);
        var config = BuildConfig(t);

        var epic = new WorkItemBuilder()
            .WithId(1).WithType(t.TopType).WithTitle("Root").WithState("Doing").Build();
        var middle = new WorkItemBuilder()
            .WithId(10).WithType(t.MiddleType).WithTitle("Middle").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType(t.LeafType).WithTitle("Leaf").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { middle });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(config);
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Facets.ShouldBe(t.TopFacets);

        result.Children.ShouldNotBeNull();
        result.Children[0].Facets.ShouldBe(t.MiddleFacets);

        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children![0].Facets.ShouldBe(t.LeafFacets);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task ThreeTier_LeafNodeHasNoChildren(string templateName)
    {
        var t = GetTemplate(templateName);
        var config = BuildConfig(t);

        var epic = new WorkItemBuilder()
            .WithId(1).WithType(t.TopType).WithTitle("Root").WithState("Doing").Build();
        var middle = new WorkItemBuilder()
            .WithId(10).WithType(t.MiddleType).WithTitle("Middle").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType(t.LeafType).WithTitle("Leaf").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { middle });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(config);
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children![0].Children.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task ThreeTier_TopLevelIsPlannable(string templateName)
    {
        var t = GetTemplate(templateName);
        var config = BuildConfig(t);

        var epic = new WorkItemBuilder()
            .WithId(1).WithType(t.TopType).WithTitle("Root").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(config);
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Facets.ShouldContain("plannable");
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task ThreeTier_LeafIsImplementable(string templateName)
    {
        var t = GetTemplate(templateName);
        var config = BuildConfig(t);

        var epic = new WorkItemBuilder()
            .WithId(1).WithType(t.TopType).WithTitle("Root").WithState("Doing").Build();
        var middle = new WorkItemBuilder()
            .WithId(10).WithType(t.MiddleType).WithTitle("Middle").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType(t.LeafType).WithTitle("Leaf").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { middle });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(config);
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        var leaf = result.Children![0].Children![0];
        leaf.Facets.ShouldContain("implementable");
        leaf.Facets.ShouldNotContain("plannable");
    }

    // ===================================================================
    // 4-tier: Epic → Feature → Issue → Task (hypothetical deep hierarchy)
    // ===================================================================

    private static readonly ProcessConfig FourTierConfig = new ProcessConfigBuilder()
        .WithProcessTemplate("Basic")
        .WithType("Epic", ["plannable"])
        .WithType("Feature", ["plannable"])
        .WithType("Issue", ["plannable", "implementable"])
        .WithType("Task", ["implementable"])
        .Build();

    [Fact]
    public async Task FourTier_WalksFullHierarchy()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(1000).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(FourTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1);
        result.Type.ShouldBe("Epic");

        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(1);
        var featureNode = result.Children[0];
        featureNode.WorkItemId.ShouldBe(10);
        featureNode.Type.ShouldBe("Feature");

        featureNode.Children.ShouldNotBeNull();
        featureNode.Children!.Length.ShouldBe(1);
        var issueNode = featureNode.Children[0];
        issueNode.WorkItemId.ShouldBe(100);
        issueNode.Type.ShouldBe("Issue");

        issueNode.Children.ShouldNotBeNull();
        issueNode.Children!.Length.ShouldBe(1);
        var taskNode = issueNode.Children[0];
        taskNode.WorkItemId.ShouldBe(1000);
        taskNode.Type.ShouldBe("Task");
    }

    [Fact]
    public async Task FourTier_AnnotatesFacetsAtEveryLevel()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(1000).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(FourTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Facets.ShouldBe(["plannable"]);

        var featureNode = result.Children![0];
        featureNode.Facets.ShouldBe(["plannable"]);

        var issueNode = featureNode.Children![0];
        issueNode.Facets.ShouldBe(["plannable", "implementable"]);

        var taskNode = issueNode.Children![0];
        taskNode.Facets.ShouldBe(["implementable"]);
    }

    [Fact]
    public async Task FourTier_LeafTaskHasNoChildren()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(1000).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(FourTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();
        var taskNode = result.Children![0].Children![0].Children![0];
        taskNode.Children.ShouldBeNull();
    }

    [Fact]
    public async Task FourTier_IntermediateLevelsArePlannable()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(1000).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(FourTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();

        // Epic is plannable-only
        result.Facets.ShouldContain("plannable");
        result.Facets.ShouldNotContain("implementable");

        // Feature is plannable-only
        var featureNode = result.Children![0];
        featureNode.Facets.ShouldContain("plannable");
        featureNode.Facets.ShouldNotContain("implementable");

        // Issue is plannable AND implementable
        var issueNode = featureNode.Children![0];
        issueNode.Facets.ShouldContain("plannable");
        issueNode.Facets.ShouldContain("implementable");

        // Task is implementable-only
        var taskNode = issueNode.Children![0];
        taskNode.Facets.ShouldContain("implementable");
        taskNode.Facets.ShouldNotContain("plannable");
    }

    [Fact]
    public async Task FourTier_WithBranchingChildren_AllAnnotatedCorrectly()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature1 = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature 1").WithState("Doing").Build();
        var feature2 = new WorkItemBuilder()
            .WithId(11).WithType("Feature").WithTitle("Feature 2").WithState("Doing").Build();
        var issue1 = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Issue 1").WithState("Doing").Build();
        var issue2 = new WorkItemBuilder()
            .WithId(101).WithType("Issue").WithTitle("Issue 2").WithState("Doing").Build();
        var task1 = new WorkItemBuilder()
            .WithId(1000).WithType("Task").WithTitle("Task 1").WithState("To Do").Build();
        var task2 = new WorkItemBuilder()
            .WithId(1001).WithType("Task").WithTitle("Task 2").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature1, feature2 });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue1 });
        _repository.GetChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue2 });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task1 });
        _repository.GetChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task2 });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());
        _repository.GetChildrenAsync(1001, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker(FourTierConfig);
        var result = await walker.WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();

        // Two features under the epic
        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(2);

        foreach (var featureNode in result.Children)
        {
            featureNode.Facets.ShouldBe(["plannable"]);
            featureNode.Children.ShouldNotBeNull();
            featureNode.Children!.Length.ShouldBe(1);

            var issueNode = featureNode.Children[0];
            issueNode.Facets.ShouldBe(["plannable", "implementable"]);
            issueNode.Children.ShouldNotBeNull();
            issueNode.Children!.Length.ShouldBe(1);

            var taskNode = issueNode.Children[0];
            taskNode.Facets.ShouldBe(["implementable"]);
            taskNode.Children.ShouldBeNull();
        }
    }
}


