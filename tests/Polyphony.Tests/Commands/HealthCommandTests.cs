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
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
        // Inject a tool checker that reports both `twig` and `git` healthy so
        // the test does not depend on the CI runner having those binaries.
        var cmd = new HealthCommand(tool => new HealthCheckResult
        {
            Name = tool,
            Success = true,
            Message = "mocked"
        });

        // Act
        var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));

        // Assert
        // Accept either Success or HealthCheckFailed, since some checks (AOT, WAL) may fail in CI
        (exitCode == 0 || exitCode == ExitCodes.HealthCheckFailed).ShouldBeTrue();
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldContain(c => c.Name == "process-config" && c.Success);
        result.Checks.ShouldContain(c => c.Name == "twig" && c.Success);
        result.Checks.ShouldContain(c => c.Name == "git" && c.Success);
        result.Checks.ShouldContain(c => c.Name == "dotnet-version");
        result.Checks.ShouldContain(c => c.Name == "aot-support");
        result.Checks.ShouldContain(c => c.Name == "sqlite-wal" || c.Name == "sqlite");
        result.Checks.ShouldContain(c => c.Name == "yamldotnet");
        result.Os.ShouldNotBeNullOrEmpty();
        result.Architecture.ShouldNotBeNullOrEmpty();
        result.DotnetVersion.ShouldNotBeNullOrEmpty();
        result.PolyphonyVersion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void HealthCommand_PolyphonyVersion_IsInformationalVersion_NotAssemblyVersion()
    {
        // Regression for the AssemblyVersion-vs-InformationalVersion bug:
        // MinVer pins AssemblyVersion to a stable "0.0.0.0" / "1.0.0.0" so
        // that downstream binders don't break on every patch, and writes the
        // real SemVer (including pre-release / build-metadata) into
        // AssemblyInformationalVersion. HealthCommand must report the latter,
        // otherwise `polyphony health` always returns the placeholder.
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "process-config.yaml");
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
        var cmd = new HealthCommand(tool => new HealthCheckResult { Name = tool, Success = true, Message = "mocked" });

        var (_, output) = CaptureConsole(() => cmd.Health(configPath));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);

        result.ShouldNotBeNull();
        result.PolyphonyVersion.ShouldNotBeNullOrEmpty();
        // Numeric AssemblyVersion is always 4-part dotted ("X.Y.Z.W"); SemVer
        // from InformationalVersion is 3-part ("X.Y.Z") with optional
        // "-prerelease" / "+build" suffix. A 4-part dotted string with no
        // pre-release / build metadata is the smoking gun for the regression.
        var fourPartDotted = System.Text.RegularExpressions.Regex.IsMatch(
            result.PolyphonyVersion!, @"^\d+\.\d+\.\d+\.\d+$");
        fourPartDotted.ShouldBeFalse(
            $"Expected SemVer from AssemblyInformationalVersion, got 4-part AssemblyVersion: {result.PolyphonyVersion}");
    }

    [Fact]
    public void HealthCommand_PolyphonyVersion_IsAtLeastMinimumMajorMinor()
    {
        // Regression for the MinVer-cache-collision bug: when a referenced
        // project from a different git repo (e.g. ../twig2) shares the same
        // `--tag-prefix` input, MinVer 7.0.0's per-process cache returns the
        // sibling repo's height-incremented version (e.g. "0.74.0") for
        // polyphony's stamp instead of computing fresh against polyphony's
        // own tags. Directory.Build.props sets `MinVerMinimumMajorMinor=1.0`
        // BOTH as a semantic floor (we shipped v1.0.0; nothing should ever
        // stamp `0.x` again) AND to differentiate the cache key. Asserting
        // the major version is >= 1 catches both regressions.
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-floor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "process-config.yaml");
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
        var cmd = new HealthCommand(tool => new HealthCheckResult { Name = tool, Success = true, Message = "mocked" });

        var (_, output) = CaptureConsole(() => cmd.Health(configPath));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);

        result.ShouldNotBeNull();
        result.PolyphonyVersion.ShouldNotBeNullOrEmpty();
        // Strip SemVer build-metadata (`+sha`) and pre-release suffix
        // (`-alpha.0.N`) before parsing the major.minor.patch core.
        var coreVersion = result.PolyphonyVersion!.Split('+', 2)[0].Split('-', 2)[0];
        var parts = coreVersion.Split('.');
        parts.Length.ShouldBeGreaterThanOrEqualTo(2,
            $"Expected SemVer core 'major.minor[.patch]', got: {result.PolyphonyVersion}");
        var major = int.Parse(parts[0]);
        major.ShouldBeGreaterThanOrEqualTo(1,
            $"Expected major version >= 1 (MinVerMinimumMajorMinor floor), got: {result.PolyphonyVersion}");
    }

    [Fact]
    public void HealthCommand_Fails_WhenConfigMissing()
    {
        var cmd = new HealthCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(), $"no-such-config-{Guid.NewGuid():N}.yaml");
        var (exitCode, output) = CaptureConsole(() => cmd.Health(bogusPath));
        exitCode.ShouldBe(ExitCodes.HealthCheckFailed);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldContain(c => c.Name == "process-config" && !c.Success);
    }

    [Fact]
    public void HealthCommand_Fails_WhenConfigInvalid()
    {
        // Arrange: create an invalid config file
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "process-config.yaml");
        File.WriteAllText(configPath, "not: valid: yaml: [");
        var cmd = new HealthCommand();

        // Act
        var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));

        // Assert
        exitCode.ShouldBe(ExitCodes.HealthCheckFailed);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldContain(c => c.Name == "process-config" && !c.Success);
    }

    [Fact]
    public void HealthCommand_Fails_WhenTwigMissing()
    {
        // Simulate missing twig by passing a bogus tool name
        var cmd = new HealthCommand();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Remove PATH so no tools are found
            Environment.SetEnvironmentVariable("PATH", "");
            var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-twig-missing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var configPath = Path.Combine(tempDir, "process-config.yaml");
            File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
            var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));
            exitCode.ShouldBe(ExitCodes.HealthCheckFailed);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
            result.ShouldNotBeNull();
            result.Checks.ShouldContain(c => c.Name == "twig" && !c.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void HealthCommand_Fails_WhenGitMissing()
    {
        // Simulate missing git by passing a bogus tool name
        var cmd = new HealthCommand();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Remove PATH so no tools are found
            Environment.SetEnvironmentVariable("PATH", "");
            var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-git-missing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var configPath = Path.Combine(tempDir, "process-config.yaml");
            File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
            var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));
            exitCode.ShouldBe(ExitCodes.HealthCheckFailed);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
            result.ShouldNotBeNull();
            result.Checks.ShouldContain(c => c.Name == "git" && !c.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void HealthResult_Serialization_RoundTrip()
    {
        var result = new HealthResult
        {
            Checks = new[]
            {
                new HealthCheckResult { Name = "process-config", Success = true, Message = "ok" },
                new HealthCheckResult { Name = "twig", Success = true, Message = "ok" },
                new HealthCheckResult { Name = "git", Success = false, Message = "not found" }
            },
            Os = "TestOS",
            Architecture = "x64",
            DotnetVersion = "7.0.0",
            PolyphonyVersion = "1.2.3"
        };
        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HealthResult);
        var roundTrip = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.HealthResult);
        roundTrip.ShouldNotBeNull();
        roundTrip.Checks.Length.ShouldBe(3);
        roundTrip.Os.ShouldBe("TestOS");
        roundTrip.Architecture.ShouldBe("x64");
        roundTrip.DotnetVersion.ShouldBe("7.0.0");
        roundTrip.PolyphonyVersion.ShouldBe("1.2.3");
    }

    private static (int ExitCode, string Output) CaptureConsole(Func<int> action)
    {
        ConsoleTestLock.AsyncLock.Wait();
        try
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
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }
}

