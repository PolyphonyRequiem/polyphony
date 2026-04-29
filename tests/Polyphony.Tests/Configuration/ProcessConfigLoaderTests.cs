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
        var configPath = Path.Combine(TestHelpers.FindRepoRoot("twig2"), ".conductor", "process-config.yaml");

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
        config.Types["Epic"].Capabilities.ShouldContain("plannable");
        config.Types["Task"].Capabilities.ShouldContain("implementable");
        config.Transitions.ShouldContainKey("Epic");
        config.Transitions["Epic"]["begin_planning"].ShouldBe("Doing");
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
