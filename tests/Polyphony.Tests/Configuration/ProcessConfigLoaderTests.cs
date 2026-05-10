using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProcessConfigLoaderTests
{
    [Fact]
    public void Load_BasicProcessConfig_ParsesTypes()
    {
        // Arrange — use twig's actual config
        var configPath = Path.Combine(TestHelpers.FindRepoRoot("twig2"), ".polyphony-config", "process-config.yaml");

        if (!File.Exists(configPath))
        {
            // Skip if twig2 repo not available (CI environment)
            return;
        }

        // Act
        var config = ProcessConfigLoader.Load(configPath);

        // Assert
        config.ProcessTemplate.ShouldBe("Basic");
        config.Types.ShouldContainKey("Epic");
        config.Types.ShouldContainKey("Issue");
        config.Types.ShouldContainKey("Task");
        config.Types["Epic"].Facets.ShouldContain("plannable");
        config.Types["Task"].Facets.ShouldContain("implementable");
        config.Transitions.ShouldContainKey("Epic");
        config.Transitions["Epic"]["begin_planning"].ShouldBe("Doing");
    }

    [Fact]
    public void Load_SchemaVersion1_LoadsSuccessfully()
    {
        var path = WriteTempConfig("""
            schema_version: 1
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions: {}
            """);

        var config = ProcessConfigLoader.Load(path);

        config.SchemaVersion.ShouldBe(1);
        config.ProcessTemplate.ShouldBe("Basic");
    }

    [Fact]
    public void Load_SchemaVersion99_ThrowsWithDescriptiveMessage()
    {
        var path = WriteTempConfig("""
            schema_version: 99
            process_template: Basic
            types: {}
            transitions: {}
            """);

        var ex = Should.Throw<InvalidOperationException>(() => ProcessConfigLoader.Load(path));
        ex.Message.ShouldContain("99");
        ex.Message.ShouldContain("Unsupported");
    }

    [Fact]
    public void Load_AbsentSchemaVersion_DefaultsToZeroAndLoads()
    {
        var path = WriteTempConfig("""
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions: {}
            """);

        var config = ProcessConfigLoader.Load(path);

        config.SchemaVersion.ShouldBe(0);
        config.ProcessTemplate.ShouldBe("Basic");
    }

    [Fact]
    public void Load_SelfReferential_DefaultsToFalse()
    {
        var path = WriteTempConfig("""
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions: {}
            """);

        var config = ProcessConfigLoader.Load(path);

        config.Types["Task"].SelfReferential.ShouldBeFalse();
    }

    [Fact]
    public void Load_SelfReferentialTrue_ParsesCorrectly()
    {
        var path = WriteTempConfig("""
            process_template: Basic
            types:
              Scenario:
                facets: [plannable]
                self_referential: true
            transitions: {}
            """);

        var config = ProcessConfigLoader.Load(path);

        config.Types["Scenario"].SelfReferential.ShouldBeTrue();
    }

    [Fact]
    public void Load_TypeWithParent_ParsesParentCorrectly()
    {
        var path = WriteTempConfig("""
            process_template: Basic
            types:
              Feature:
                facets: [plannable]
              Story:
                facets: [implementable]
                parent: Feature
            transitions: {}
            """);

        var config = ProcessConfigLoader.Load(path);
        config.Types["Story"].Parent.ShouldBe("Feature");
        config.Types["Feature"].Parent.ShouldBeNull();
    }

    [Fact]
    public void GetParentTypeName_ReturnsParentOrNull()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Facets = new[] { "plannable" } },
                ["Feature"] = new TypeConfig { Facets = new[] { "plannable" }, Parent = "Epic" },
                ["Task"] = new TypeConfig { Facets = new[] { "implementable" }, Parent = "Feature" }
            }
        };

        ProcessConfigLoader.GetParentTypeName(config, "Epic").ShouldBeNull();
        ProcessConfigLoader.GetParentTypeName(config, "Feature").ShouldBe("Epic");
        ProcessConfigLoader.GetParentTypeName(config, "Task").ShouldBe("Feature");
    }

    [Fact]
    public void GetParentTypeName_ThrowsForUnknownType()
    {
        var config = new ProcessConfig { Types = new() };
        Should.Throw<ArgumentException>(() => ProcessConfigLoader.GetParentTypeName(config, "Unknown"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G2 retired-key rejection (no_window_fail_loud)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pg_branch", "branch_strategy.merge_group_branch", """
        process_template: Basic
        types: { Task: { facets: [implementable] } }
        transitions: {}
        branch_strategy:
          feature_branch: "feature/{root_id}"
          pg_branch: "pg-{n}/{root_id}-{slug}"
          target: main
        """)]
    [InlineData("mg_branch", "branch_strategy.merge_group_branch", """
        process_template: Basic
        types: { Task: { facets: [implementable] } }
        transitions: {}
        branch_strategy:
          feature_branch: "feature/{root_id}"
          mg_branch: "mg-{n}/{root_id}-{slug}"
          target: main
        """)]
    [InlineData("pg_pr", "review_policies.<section>.merge_group_pr", """
        process_template: Basic
        types: { Task: { facets: [implementable] } }
        transitions: {}
        review_policies:
          implementation:
            pg_pr: { agent_review: true, human_review: false, auto_merge: true }
        """)]
    [InlineData("mg_pr", "review_policies.<section>.merge_group_pr", """
        process_template: Basic
        types: { Task: { facets: [implementable] } }
        transitions: {}
        review_policies:
          implementation:
            mg_pr: { agent_review: true, human_review: false, auto_merge: true }
        """)]
    public void Load_RetiredKey_ThrowsWithRenameGuidance(
        string retiredKey, string replacementKey, string yaml)
    {
        var path = WriteTempConfig(yaml);
        var ex = Should.Throw<InvalidOperationException>(() => ProcessConfigLoader.Load(path));
        ex.Message.ShouldContain($"'{retiredKey}'");
        ex.Message.ShouldContain($"'{replacementKey}'");
        ex.Message.ShouldContain("Polyphony 2.4.0");
    }

    private static string WriteTempConfig(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}

internal static class TestHelpers
{
    public static string FindRepoRoot(string repoName)
    {
        // Walk up from test execution directory to find sibling repos
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            var sibling = Path.Combine(parent, repoName);
            if (Directory.Exists(sibling) && Directory.Exists(Path.Combine(sibling, ".git")))
                return sibling;
            dir = parent;
        }
        // Fallback: assume standard sibling layout
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..", repoName));
    }
}

