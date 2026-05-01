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
        config.Types["Epic"].Capabilities.ShouldContain("plannable");
        config.Types["Issue"].Capabilities.ShouldContain("plannable");
        config.Types["Issue"].Capabilities.ShouldContain("implementable");
        config.Types["Task"].Capabilities.ShouldContain("implementable");
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
        config.Types["User Story"].Capabilities.ShouldContain("plannable");
        config.Types["User Story"].Capabilities.ShouldContain("implementable");
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
        config.Types["Product Backlog Item"].Capabilities.ShouldContain("plannable");
        config.Types["Product Backlog Item"].Capabilities.ShouldContain("implementable");
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
        config.Types["Requirement"].Capabilities.ShouldContain("plannable");
        config.Types["Requirement"].Capabilities.ShouldContain("implementable");
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
        config.BranchStrategy.PgBranch.ShouldNotBeNullOrWhiteSpace();
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
        config.Types["Epic"].Capabilities.ShouldContain("plannable");
        config.Types["Epic"].Capabilities.ShouldNotContain("implementable");
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
        config.Types["Task"].Capabilities.ShouldContain("implementable");
        config.Types["Task"].Capabilities.ShouldNotContain("plannable");
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
