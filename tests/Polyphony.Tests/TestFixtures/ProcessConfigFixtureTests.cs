using Polyphony.Configuration;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.TestFixtures;

public sealed class ProcessConfigFixtureTests
{
    [Fact]
    public void Basic_ReturnsValidConfig()
    {
        var config = ProcessConfigFixture.Basic();

        config.ShouldNotBeNull();
        config.ProcessTemplate.ShouldBe("Basic");
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("Issue");
        config.Types.ShouldContainKey("Task");
        config.Types["Epic"].Facets.ShouldContain("plannable");
        config.Types["Issue"].Facets.ShouldContain("plannable");
        config.Types["Issue"].Facets.ShouldContain("implementable");
        config.Types["Task"].Facets.ShouldContain("implementable");
        config.Transitions.ShouldNotBeEmpty();
    }

    [Fact]
    public void Agile_ReturnsValidConfig()
    {
        var config = ProcessConfigFixture.Agile();

        config.ShouldNotBeNull();
        config.ProcessTemplate.ShouldBe("Agile");
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("User Story");
        config.Types.ShouldContainKey("Task");
        config.Types["User Story"].Facets.ShouldContain("plannable");
        config.Types["User Story"].Facets.ShouldContain("implementable");
        config.Transitions.ShouldNotBeEmpty();
    }

    [Fact]
    public void Scrum_ReturnsValidConfig()
    {
        var config = ProcessConfigFixture.Scrum();

        config.ShouldNotBeNull();
        config.ProcessTemplate.ShouldBe("Scrum");
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("Product Backlog Item");
        config.Types.ShouldContainKey("Task");
        config.Types["Product Backlog Item"].Facets.ShouldContain("plannable");
        config.Types["Product Backlog Item"].Facets.ShouldContain("implementable");
        config.Transitions.ShouldNotBeEmpty();
    }

    [Fact]
    public void Cmmi_ReturnsValidConfig()
    {
        var config = ProcessConfigFixture.Cmmi();

        config.ShouldNotBeNull();
        config.ProcessTemplate.ShouldBe("CMMI");
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("Requirement");
        config.Types.ShouldContainKey("Task");
        config.Types["Requirement"].Facets.ShouldContain("plannable");
        config.Types["Requirement"].Facets.ShouldContain("implementable");
        config.Transitions.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Agile")]
    [InlineData("Scrum")]
    [InlineData("CMMI")]
    public void AllTemplates_HaveBranchStrategy(string template)
    {
        var config = LoadByTemplate(template);

        config.BranchStrategy.ShouldNotBeNull();
        config.BranchStrategy!.Target.ShouldBe("main");
        config.BranchStrategy.FeatureBranch.ShouldNotBeNullOrWhiteSpace();
        config.BranchStrategy.MergeGroupBranch.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Agile")]
    [InlineData("Scrum")]
    [InlineData("CMMI")]
    public void AllTemplates_HaveEpicAsPlannable(string template)
    {
        var config = LoadByTemplate(template);

        config.Types.ShouldContainKey("Epic");
        config.Types["Epic"].Facets.ShouldContain("plannable");
        config.Types["Epic"].Facets.ShouldNotContain("implementable");
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Agile")]
    [InlineData("Scrum")]
    [InlineData("CMMI")]
    public void AllTemplates_HaveTaskAsImplementable(string template)
    {
        var config = LoadByTemplate(template);

        config.Types.ShouldContainKey("Task");
        config.Types["Task"].Facets.ShouldContain("implementable");
        config.Types["Task"].Facets.ShouldNotContain("plannable");
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Agile")]
    [InlineData("Scrum")]
    [InlineData("CMMI")]
    public void AllTemplates_HaveThreeTypes(string template)
    {
        var config = LoadByTemplate(template);

        config.Types.Count.ShouldBe(3);
    }

    private static ProcessConfig LoadByTemplate(string template) => template switch
    {
        "Basic" => ProcessConfigFixture.Basic(),
        "Agile" => ProcessConfigFixture.Agile(),
        "Scrum" => ProcessConfigFixture.Scrum(),
        "CMMI" => ProcessConfigFixture.Cmmi(),
        _ => throw new ArgumentException($"Unknown template: {template}"),
    };
}

