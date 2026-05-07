using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;
using NSubstitute;

namespace Polyphony.Tests.Commands;

public sealed class StateCommandsPreflightTests : CommandTestBase
{
    private (StateCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new StateCommands(twig, git, gh, runner, Repository, Config), runner);
    } // End of CreateCommand

    // (Removed duplicate orphaned block)


    private static void StubGitTopLevel(FakeProcessRunner runner, string? path)
        => runner.WhenExact("git", ["rev-parse", "--show-toplevel"],
            new ProcessResult(path is null ? 128 : 0, path ?? "", path is null ? "fatal: not a git repo" : ""));

    private static void StubTwigVersion(FakeProcessRunner runner, string? version)
        => runner.WhenExact("twig", ["--version"],
            new ProcessResult(version is null ? 1 : 0, version ?? "", ""));

    private static void StubTwigConfig(FakeProcessRunner runner, string key, string? value)
    {
        var json = value is null ? "{}" : $$"""{"info":"{{value}}"}""";
        runner.WhenExact("twig", ["config", key, "--output", "json"],
            new ProcessResult(value is null ? 1 : 0, json, ""));
    }

    private static void StubTwigShow(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"work_item_id":{{id}},"title":"{{title}}"}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, title is null ? "not found" : ""));
    }

    private static void StubGhAuth(FakeProcessRunner runner, bool ok)
        => runner.WhenExact("gh", ["auth", "status"],
            new ProcessResult(ok ? 0 : 1, "", ok ? "Logged in to github.com" : "Not logged in"));

    private static void StubDotnetVersion(FakeProcessRunner runner, string? version)
        => runner.WhenExact("dotnet", ["--version"],
            new ProcessResult(version is null ? 1 : 0, version ?? "", ""));

    [Fact]
    public async Task PreflightLite_AllPass_EmitsReady()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;
        result.Ready.ShouldBeTrue($"Output was: {output}");
        result.FailedCount.ShouldBe(0);
        result.Checks.Count.ShouldBe(3);
        result.Checks.ShouldContain(c => c.Name == "git_repo" && c.Passed);
        result.Checks.ShouldContain(c => c.Name == "twig_cli" && c.Passed);
        result.Checks.ShouldContain(c => c.Name == "polyphony_cli" && c.Passed);
    }

    [Fact]
    public async Task PreflightLite_NoGit_FailsAndIncludesRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, null);
        StubTwigVersion(runner, "twig 0.42.0");

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Ready.ShouldBeFalse();
        result.FailedCount.ShouldBe(1);
        var gitCheck = result.Checks.First(c => c.Name == "git_repo");
        gitCheck.Passed.ShouldBeFalse();
        gitCheck.Remediation.ShouldNotBeNull();
    }

    [Fact]
    public async Task Preflight_AllPass_EmitsReadyWithDetails()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello World");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;
        result.Ready.ShouldBeTrue($"Output was: {output}");
        result.FailedCount.ShouldBe(0);
        result.WarningCount.ShouldBe(0);
        result.RequiredChecks.Count.ShouldBe(4);
        result.AdvisoryChecks.Count.ShouldBe(3);
        result.Details.WorkItemId.ShouldBe(100);
        result.Details.AdoOrg.ShouldBe("myorg");
        result.Details.AdoProject.ShouldBe("myproj");
    }

    [Fact]
    public async Task Preflight_NoAdoAccess_FailsRequired()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, null);
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        result.Ready.ShouldBeFalse();
        result.FailedCount.ShouldBe(1);
        result.RequiredChecks.First(c => c.Name == "ado_access").Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task Preflight_GhAuthFailure_RequiredChecksStillPass()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, false);
        StubDotnetVersion(runner, "9.0.100");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        result.Ready.ShouldBeTrue($"Output was: {output}");
        result.WarningCount.ShouldBeGreaterThanOrEqualTo(1);
        result.AdvisoryChecks.First(c => c.Name == "gh_auth").Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task PreflightLite_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());

        output.ShouldContain("\"ready\"");
        output.ShouldContain("\"failed_count\"");
        output.ShouldContain("\"checks\"");
        output.ShouldNotContain("\"FailedCount\"", Case.Sensitive);
        output.ShouldNotContain("\"Checks\"", Case.Sensitive);
    }

    [Fact]
    public async Task Preflight_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));

        output.ShouldContain("\"required_checks\"");
        output.ShouldContain("\"advisory_checks\"");
        output.ShouldContain("\"warning_count\"");
        output.ShouldContain("\"failed_count\"");
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"ado_org\"");
        output.ShouldContain("\"ado_project\"");
        output.ShouldNotContain("\"RequiredChecks\"", Case.Sensitive);
        output.ShouldNotContain("\"AdoOrg\"", Case.Sensitive);
    }

    // ---- WI-3: --workflow-yaml / --required-version flag wiring ----

    [Fact]
    public async Task PreflightLite_NoVersionFlags_SkipsVersionCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Checks.Any(c => c.Name == "polyphony_version").ShouldBeFalse(
            "version check is opt-in — without flags it must not appear");
        result.Checks.Count.ShouldBe(3);
    }

    [Fact]
    public async Task PreflightLite_RequiredVersionBelowCurrent_PassesWithCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        // 0.0.1 will pass against any 1.x.y polyphony build (and the test
        // host always runs >= 1.0.0 thanks to MinVerMinimumMajorMinor).
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PreflightLite(workflowYaml: null, requiredVersion: "0.0.1"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Ready.ShouldBeTrue($"Output was: {output}");
        var versionCheck = result.Checks.FirstOrDefault(c => c.Name == "polyphony_version");
        versionCheck.ShouldNotBeNull();
        versionCheck.Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task PreflightLite_RequiredVersionAboveCurrent_FailsWithRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        // 99.0.0 cannot be satisfied by any realistic build.
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PreflightLite(workflowYaml: null, requiredVersion: "99.0.0"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Ready.ShouldBeFalse();
        var versionCheck = result.Checks.First(c => c.Name == "polyphony_version");
        versionCheck.Passed.ShouldBeFalse();
        versionCheck.Remediation.ShouldNotBeNullOrWhiteSpace();
        versionCheck.Remediation!.ShouldContain("Update polyphony CLI");
    }

    [Fact]
    public async Task PreflightLite_WorkflowYamlPath_ReadsMetadata()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        var path = WriteTempWorkflowYaml("0.0.1");
        try
        {
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.PreflightLite(workflowYaml: path, requiredVersion: null));
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

            var versionCheck = result.Checks.FirstOrDefault(c => c.Name == "polyphony_version");
            versionCheck.ShouldNotBeNull("metadata.min_polyphony_version should be picked up from the YAML");
            versionCheck.Passed.ShouldBeTrue($"Output was: {output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PreflightLite_BothFlags_RequiredVersionWins()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        // YAML says 0.0.1 (would pass), explicit flag says 99.0.0 (fails).
        // The explicit flag must win — testing seam contract.
        var path = WriteTempWorkflowYaml("0.0.1");
        try
        {
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.PreflightLite(workflowYaml: path, requiredVersion: "99.0.0"));
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

            result.Ready.ShouldBeFalse("--required-version 99.0.0 must override the YAML's 0.0.1");
            result.Checks.First(c => c.Name == "polyphony_version").Passed.ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PreflightLite_WorkflowYamlMissingMetadata_SkipsCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");

        var path = Path.Combine(Path.GetTempPath(),
            $"polyphony-state-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "workflow:\n  name: test\n  version: \"1.0.0\"\nagents: []\n");
        try
        {
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.PreflightLite(workflowYaml: path, requiredVersion: null));
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

            result.Ready.ShouldBeTrue($"Output was: {output}");
            result.Checks.Any(c => c.Name == "polyphony_version").ShouldBeFalse(
                "missing metadata is opt-in skip, not failure");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Preflight_VersionMismatch_AppendedToRequiredChecks()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Preflight(workItem: 100, workflowYaml: null, requiredVersion: "99.0.0"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        result.Ready.ShouldBeFalse();
        result.RequiredChecks.Count.ShouldBe(5, "version check appended to the required-checks list");
        result.RequiredChecks.First(c => c.Name == "polyphony_version").Passed.ShouldBeFalse();
    }

    private static string WriteTempWorkflowYaml(string minPolyphonyVersion)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"polyphony-state-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $"""
            workflow:
              name: test
              version: "{minPolyphonyVersion}"
              metadata:
                min_polyphony_version: "{minPolyphonyVersion}"
            agents: []
            """);
        return path;
    }
}
