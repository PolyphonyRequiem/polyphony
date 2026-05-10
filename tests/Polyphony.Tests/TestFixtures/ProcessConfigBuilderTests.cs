using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.TestFixtures;

public sealed class ProcessConfigBuilderTests
{
    [Fact]
    public void Build_Defaults_ProducesValidConfig()
    {
        var config = new ProcessConfigBuilder().Build();

        config.ProcessTemplate.ShouldBe("Basic");
        config.Platform.ShouldBe("github");
        config.Types.ShouldBeEmpty();
        config.Transitions.ShouldBeEmpty();
        config.BranchStrategy.ShouldBeNull();
    }

    [Fact]
    public void Build_WithType_AddsTypeAndTransitions()
    {
        var transitions = new Dictionary<string, string>
        {
            ["begin_planning"] = "Doing",
            ["complete"] = "Done",
        };

        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], transitions)
            .Build();

        config.Types.ShouldContainKey("Issue");
        config.Types["Issue"].Facets.ShouldBe(new[] { "plannable", "implementable" });
        config.Transitions.ShouldContainKey("Issue");
        config.Transitions["Issue"]["begin_planning"].ShouldBe("Doing");
        config.Transitions["Issue"]["complete"].ShouldBe("Done");
    }

    [Fact]
    public void Build_WithTypeNoTransitions_AddsTypeOnly()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Task", ["implementable"])
            .Build();

        config.Types.ShouldContainKey("Task");
        config.Types["Task"].Facets.ShouldBe(new[] { "implementable" });
        config.Transitions.ShouldNotContainKey("Task");
    }

    [Fact]
    public void Build_MultipleTypes_AddsAll()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithType("Task", ["implementable"])
            .Build();

        config.Types.Count.ShouldBe(2);
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("Task");
    }

    [Fact]
    public void Build_WithBranchStrategy_SetsAllFields()
    {
        var config = new ProcessConfigBuilder()
            .WithBranchStrategy(
                featureBranch: "feature/{id}",
                planningBranch: "planning/{id}",
                MergeGroupBranch: "feature/{id}-mg-{n}",
                target: "develop")
            .Build();

        config.BranchStrategy.ShouldNotBeNull();
        config.BranchStrategy!.FeatureBranch.ShouldBe("feature/{id}");
        config.BranchStrategy.PlanningBranch.ShouldBe("planning/{id}");
        config.BranchStrategy.MergeGroupBranch.ShouldBe("feature/{id}-mg-{n}");
        config.BranchStrategy.PgBranch.ShouldBe("");
        config.BranchStrategy.Target.ShouldBe("develop");
    }

    [Fact]
    public void Build_WithBranchStrategy_DefaultValues()
    {
        var config = new ProcessConfigBuilder()
            .WithBranchStrategy()
            .Build();

        config.BranchStrategy.ShouldNotBeNull();
        config.BranchStrategy!.Target.ShouldBe("main");
    }

    [Fact]
    public void Build_WithProcessTemplate_SetsTemplate()
    {
        var config = new ProcessConfigBuilder()
            .WithProcessTemplate("Agile")
            .Build();

        config.ProcessTemplate.ShouldBe("Agile");
    }

    [Fact]
    public void Build_WithPlatform_SetsPlatform()
    {
        var config = new ProcessConfigBuilder()
            .WithPlatform("azure-devops")
            .Build();

        config.Platform.ShouldBe("azure-devops");
    }
}

