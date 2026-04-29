using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Polyphony.Tests.TestFixtures;

public sealed class WorkItemBuilderTests
{
    [Fact]
    public void Build_Defaults_ProducesIssueInToDo()
    {
        var item = new WorkItemBuilder().Build();

        item.Id.ShouldBe(1);
        item.Title.ShouldBe("Test Item");
        item.Type.ShouldBe(WorkItemType.Issue);
        item.State.ShouldBe("To Do");
        item.ParentId.ShouldBeNull();
        item.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Build_FullyConfigured_SetsAllProperties()
    {
        var item = new WorkItemBuilder()
            .WithId(42)
            .WithTitle("Login endpoint")
            .WithType("Epic")
            .WithState("Doing")
            .WithParentId(100)
            .Build();

        item.Id.ShouldBe(42);
        item.Title.ShouldBe("Login endpoint");
        item.Type.ShouldBe(WorkItemType.Epic);
        item.State.ShouldBe("Doing");
        item.ParentId.ShouldBe(100);
    }

    [Fact]
    public void Build_WithType_ParsesKnownTypes()
    {
        new WorkItemBuilder().WithType("Task").Build().Type.ShouldBe(WorkItemType.Task);
        new WorkItemBuilder().WithType("Bug").Build().Type.ShouldBe(WorkItemType.Bug);
        new WorkItemBuilder().WithType("Epic").Build().Type.ShouldBe(WorkItemType.Epic);
        new WorkItemBuilder().WithType("Feature").Build().Type.ShouldBe(WorkItemType.Feature);
    }

    [Fact]
    public void Build_InvalidType_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => new WorkItemBuilder().WithType("").Build());
    }

    [Fact]
    public void Build_NullParentId_IsNull()
    {
        var item = new WorkItemBuilder().WithParentId(null).Build();

        item.ParentId.ShouldBeNull();
    }

    [Fact]
    public void BuildAll_CreatesRootAndChildren()
    {
        var child1 = new WorkItemBuilder().WithId(10).WithTitle("Child 1").WithType("Task");
        var child2 = new WorkItemBuilder().WithId(11).WithTitle("Child 2").WithType("Task");

        var (root, children) = new WorkItemBuilder()
            .WithId(1)
            .WithType("Issue")
            .WithChildren(child1, child2)
            .BuildAll();

        root.Id.ShouldBe(1);
        children.Count.ShouldBe(2);
        children[0].ParentId.ShouldBe(1);
        children[1].ParentId.ShouldBe(1);
        children[0].Id.ShouldBe(10);
        children[1].Id.ShouldBe(11);
    }

    [Fact]
    public void BuildAll_NoChildren_ReturnsEmptyList()
    {
        var (root, children) = new WorkItemBuilder().WithId(5).BuildAll();

        root.Id.ShouldBe(5);
        children.ShouldBeEmpty();
    }
}
