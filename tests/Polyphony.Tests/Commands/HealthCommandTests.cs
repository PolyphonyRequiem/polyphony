using System.Text.Json;
using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class HealthCommandTests
{
    [Fact]
    public void HealthCommand_Success_WhenAllHealthy()
    {
        // Arrange: create a temp config file
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "process-config.yaml");
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { capabilities: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
        var cmd = new HealthCommand();

        // Act
        var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));

        // Assert
        exitCode.ShouldBe(0);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldContain(c => c.Name == "process-config" && c.Success);
        result.Checks.ShouldContain(c => c.Name == "twig");
        result.Checks.ShouldContain(c => c.Name == "git");
        result.Os.ShouldNotBeNullOrEmpty();
        result.Architecture.ShouldNotBeNullOrEmpty();
        result.DotnetVersion.ShouldNotBeNullOrEmpty();
        result.PolyphonyVersion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void HealthCommand_Fails_WhenConfigMissing()
    {
        var cmd = new HealthCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(), $"no-such-config-{Guid.NewGuid():N}.yaml");
        var (exitCode, output) = CaptureConsole(() => cmd.Health(bogusPath));
        exitCode.ShouldBe(4);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldContain(c => c.Name == "process-config" && !c.Success);
    }

    private static (int ExitCode, string Output) CaptureConsole(Func<int> action)
    {
        lock (ConsoleTestLock.Lock)
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = action();
                return (exitCode, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}
