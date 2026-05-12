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
        var planObserver = new Polyphony.Sdlc.Observers.PlanObserver(git, gh, twig);
        return (new StateCommands(twig, git, gh, runner, Repository, Config, planObserver), runner);
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

    /// <summary>
    /// Stubs both the common-dir resolution and the is-bare-repository
    /// probe used by the bare_repo preflight advisory check (AB#3093).
    /// Default <paramref name="isBare"/>=true so existing happy-path
    /// tests do not gain an unexpected advisory warning.
    /// </summary>
    private static void StubBareRepo(FakeProcessRunner runner, bool isBare = true, string commonDir = "/repo/.git")
    {
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, commonDir, ""));
        runner.WhenExact("git", ["--git-dir", commonDir, "rev-parse", "--is-bare-repository"],
            new ProcessResult(0, isBare ? "true" : "false", ""));
    }

    /// <summary>
    /// Stubs the common-dir resolution to fail (simulates "not in a git
    /// repo" or other rev-parse failure).
    /// </summary>
    private static void StubBareRepoCommonDirFails(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(128, "", "fatal: not a git repository"));
    }

    /// <summary>
    /// Stubs common-dir resolution OK but is-bare-repository failing
    /// (simulates safe.bareRepository=explicit on a misconfigured probe,
    /// or a corrupted gitdir).
    /// </summary>
    private static void StubBareRepoIsBareFails(FakeProcessRunner runner, string commonDir = "/repo/.git")
    {
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, commonDir, ""));
        runner.WhenExact("git", ["--git-dir", commonDir, "rev-parse", "--is-bare-repository"],
            new ProcessResult(128, "", "fatal: cannot use bare repository '...' (safe.bareRepository is 'explicit')"));
    }

    [Fact]
    public async Task PreflightLite_AllPass_EmitsReady()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubBareRepo(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;
        result.Ready.ShouldBeTrue($"Output was: {output}");
        result.FailedCount.ShouldBe(0);
        result.Checks.Count.ShouldBe(4);
        result.Checks.ShouldContain(c => c.Name == "git_repo" && c.Passed);
        result.Checks.ShouldContain(c => c.Name == "twig_cli" && c.Passed);
        result.Checks.ShouldContain(c => c.Name == "polyphony_cli" && c.Passed);
        result.Checks.ShouldContain(c => c.Name == "bare_repo" && c.Passed);
    }

    [Fact]
    public async Task PreflightLite_NoGit_FailsAndIncludesRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, null);
        StubTwigVersion(runner, "twig 0.42.0");
        StubBareRepo(runner);

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
        StubBareRepo(runner, isBare: true);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;
        result.Ready.ShouldBeTrue($"Output was: {output}");
        result.FailedCount.ShouldBe(0);
        result.WarningCount.ShouldBe(0);
        result.RequiredChecks.Count.ShouldBe(5);
        result.AdvisoryChecks.Count.ShouldBe(3);
        result.RequiredChecks.ShouldContain(c => c.Name == "bare_repo" && c.Passed);
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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Checks.Any(c => c.Name == "polyphony_version").ShouldBeFalse(
            "version check is opt-in — without flags it must not appear");
        result.Checks.Count.ShouldBe(4);
    }

    [Fact]
    public async Task PreflightLite_RequiredVersionBelowCurrent_PassesWithCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

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
        StubBareRepo(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Preflight(workItem: 100, workflowYaml: null, requiredVersion: "99.0.0"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        result.Ready.ShouldBeFalse();
        result.RequiredChecks.Count.ShouldBe(6, "version check appended to the required-checks list");
        result.RequiredChecks.First(c => c.Name == "polyphony_version").Passed.ShouldBeFalse();
    }

    // ---- AB#3093 + AB#3085: bare_repo required check (flipped from advisory) ----

    [Fact]
    public async Task Preflight_BareCommonDir_BareRepoRequiredPasses()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");
        StubBareRepo(runner, isBare: true, commonDir: "/projects/polyphony.git");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        result.Ready.ShouldBeTrue($"Output was: {output}");
        var bareCheck = result.RequiredChecks.First(c => c.Name == "bare_repo");
        bareCheck.Passed.ShouldBeTrue();
        bareCheck.Detail.ShouldContain("/projects/polyphony.git");
        bareCheck.Detail.ShouldContain("AB#3085");
        bareCheck.Remediation.ShouldBeNull("passing checks should not carry a remediation hint");
    }

    [Fact]
    public async Task Preflight_NonBareCommonDir_BareRepoRequiredFailsWithDocLink()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");
        StubBareRepo(runner, isBare: false, commonDir: "/repo/.git");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        // bare_repo is now REQUIRED — a non-bare layout must gate the workflow.
        result.Ready.ShouldBeFalse($"bare_repo failure must gate the workflow. Output was: {output}");
        result.FailedCount.ShouldBeGreaterThanOrEqualTo(1);

        var bareCheck = result.RequiredChecks.First(c => c.Name == "bare_repo");
        bareCheck.Passed.ShouldBeFalse();
        bareCheck.Detail.ShouldContain("/repo/.git");
        bareCheck.Detail.ShouldContain("legacy layout");
        bareCheck.Detail.ShouldContain("AB#3085");
        bareCheck.Remediation.ShouldNotBeNull();
        bareCheck.Remediation!.ShouldContain("docs/per-run-worktree-layout.md");
        bareCheck.Remediation.ShouldContain("AB#3085");
    }

    [Fact]
    public async Task Preflight_CommonDirProbeFails_BareRepoRequiredFailsWithRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");
        StubBareRepoCommonDirFails(runner);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        // Common-dir probe failure surfaces as a required failure, not a hard
        // crash — the verb still emits a complete preflight envelope.
        var bareCheck = result.RequiredChecks.First(c => c.Name == "bare_repo");
        bareCheck.Passed.ShouldBeFalse();
        bareCheck.Detail.ShouldContain("Not inside a git repository");
        bareCheck.Remediation.ShouldNotBeNull();
        bareCheck.Remediation!.ShouldContain("docs/per-run-worktree-layout.md");
    }

    [Fact]
    public async Task Preflight_IsBareProbeFails_BareRepoRequiredFailsWithRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");
        StubTwigShow(runner, 100, "Hello");
        StubGhAuth(runner, true);
        StubDotnetVersion(runner, "9.0.100");
        StubBareRepoIsBareFails(runner, commonDir: "/repo/.git");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Preflight(workItem: 100));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightResult)!;

        var bareCheck = result.RequiredChecks.First(c => c.Name == "bare_repo");
        bareCheck.Passed.ShouldBeFalse();
        bareCheck.Detail.ShouldContain("/repo/.git");
        bareCheck.Detail.ShouldContain("safe.bareRepository");
        bareCheck.Remediation.ShouldNotBeNull();
        bareCheck.Remediation!.ShouldContain("docs/per-run-worktree-layout.md");
    }

    [Fact]
    public async Task PreflightLite_IncludesBareRepoCheck()
    {
        // bare_repo flipped from advisory→required as of AB#3085 (per-run
        // worktree epic) and is wired into BOTH preflight and preflight-lite.
        // Operators on legacy non-bare layouts are expected to run
        // scripts/Migrate-ToBareRepo.ps1 before invoking the SDLC.
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubBareRepo(runner, isBare: true, commonDir: "/projects/polyphony.git");

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Checks.Any(c => c.Name == "bare_repo").ShouldBeTrue(
            "preflight-lite must include bare_repo as of AB#3085 (advisory→required flip)");
        result.Checks.First(c => c.Name == "bare_repo").Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task PreflightLite_NonBareCommonDir_BareRepoFails()
    {
        var (cmd, runner) = CreateCommand();
        StubGitTopLevel(runner, "/repo");
        StubTwigVersion(runner, "twig 0.42.0");
        StubBareRepo(runner, isBare: false, commonDir: "/repo/.git");

        var (_, output) = await CaptureConsoleAsync(() => cmd.PreflightLite());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatePreflightLiteResult)!;

        result.Ready.ShouldBeFalse("non-bare layout must gate preflight-lite as of AB#3085");
        result.FailedCount.ShouldBeGreaterThanOrEqualTo(1);
        var bareCheck = result.Checks.First(c => c.Name == "bare_repo");
        bareCheck.Passed.ShouldBeFalse();
        bareCheck.Remediation.ShouldNotBeNull();
        bareCheck.Remediation!.ShouldContain("docs/per-run-worktree-layout.md");
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
