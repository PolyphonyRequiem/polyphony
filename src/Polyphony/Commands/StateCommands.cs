using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Versioning;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// State-detection and preflight verbs (<c>polyphony state ...</c>).
/// Replaces the deterministic PowerShell scripts invoked from the apex
/// SDLC workflow:
/// <list type="bullet">
///   <item><c>scripts/preflight-lite.ps1</c> → <see cref="PreflightLite"/></item>
///   <item><c>scripts/preflight-check.ps1</c> → <see cref="Preflight"/></item>
///   <item><c>scripts/detect-state.ps1</c> → <see cref="Detect"/></item>
/// </list>
/// </summary>
/// <remarks>
/// All preflight verbs follow the routing-style exit contract: ALWAYS
/// return <see cref="ExitCodes.Success"/>; the workflow gates on the JSON
/// payload's <c>ready</c> flag. Errors are reported via the payload, not
/// via process exit code. <see cref="Detect"/> returns a structured error
/// payload on unexpected failure with <c>phase = "error"</c>.
/// </remarks>
public sealed partial class StateCommands(
    ITwigClient twig,
    IGitClient git,
    IGhClient gh,
    IProcessRunner runner,
    GhTokenResolver ghTokenResolver,
    PhaseDetector phaseDetector,
    TransitionValidator transitionValidator,
    HierarchyWalker hierarchyWalker,
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    private const string DotnetExe = "dotnet";

    /// <summary>
    /// Lightweight 3-check preflight for the planning sub-workflow.
    /// Validates git repo, twig CLI availability, and polyphony CLI
    /// availability (the latter is implicitly true since we ARE polyphony).
    /// </summary>
    /// <param name="workflowYaml">
    /// Optional absolute path to the workflow YAML being run (passed by
    /// conductor as <c>{{ workflow.file }}</c>). When supplied, the verb
    /// reads <c>workflow.metadata.min_polyphony_version</c> and adds a
    /// <c>polyphony_version</c> check. See WI-3 in
    /// <c>docs/decisions/versioning-strategy.md</c>.
    /// </param>
    /// <param name="requiredVersion">
    /// Optional explicit minimum polyphony version (testing seam). When
    /// both are supplied, this wins over the YAML metadata.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("preflight-lite")]
    public async Task<int> PreflightLite(
        string? workflowYaml = null,
        string? requiredVersion = null,
        CancellationToken ct = default)
    {
        StatePreflightLiteResult result;
        try
        {
            var checks = new List<PreflightCheck>
            {
                await CheckGitRepoAsync(ct).ConfigureAwait(false),
                await CheckTwigCliAsync(ct).ConfigureAwait(false),
                CheckPolyphonyCli(),
            };

            var versionCheck = TryCheckPolyphonyVersion(workflowYaml, requiredVersion);
            if (versionCheck is not null)
            {
                checks.Add(versionCheck);
            }

            var failed = checks.Count(c => !c.Passed);
            var ready = failed == 0;
            var summary = ready
                ? "All preflight lite checks passed."
                : $"{failed} required check(s) failed. Fix before proceeding.";

            result = new StatePreflightLiteResult
            {
                Ready = ready,
                Summary = summary,
                Checks = checks,
                FailedCount = failed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new StatePreflightLiteResult
            {
                Ready = false,
                Summary = $"Preflight lite error: {ex.Message}",
                Checks = [],
                FailedCount = 1,
            };
        }

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StatePreflightLiteResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Full preflight for the apex SDLC workflow. Validates required
    /// gating prerequisites (git, twig, twig config, ADO connectivity)
    /// and surfaces advisory warnings (gh auth, polyphony CLI, dotnet SDK).
    /// </summary>
    /// <param name="workItem">ADO work item ID — must be accessible via twig.</param>
    /// <param name="workflowYaml">
    /// Optional absolute path to the workflow YAML being run (passed by
    /// conductor as <c>{{ workflow.file }}</c>). When supplied, the verb
    /// reads <c>workflow.metadata.min_polyphony_version</c> and adds a
    /// <c>polyphony_version</c> required check. See WI-3 in
    /// <c>docs/decisions/versioning-strategy.md</c>.
    /// </param>
    /// <param name="requiredVersion">
    /// Optional explicit minimum polyphony version (testing seam). When
    /// both are supplied, this wins over the YAML metadata.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("preflight")]
    public async Task<int> Preflight(
        int workItem,
        string? workflowYaml = null,
        string? requiredVersion = null,
        CancellationToken ct = default)
    {
        StatePreflightResult result;
        try
        {
            var required = new List<PreflightCheck>
            {
                await CheckGitRepoAsync(ct).ConfigureAwait(false),
                await CheckTwigCliAsync(ct).ConfigureAwait(false),
            };

            var (configCheck, adoOrg, adoProject) = await CheckTwigConfigAsync(ct).ConfigureAwait(false);
            required.Add(configCheck);
            required.Add(await CheckAdoAccessAsync(workItem, ct).ConfigureAwait(false));

            var versionCheck = TryCheckPolyphonyVersion(workflowYaml, requiredVersion);
            if (versionCheck is not null)
            {
                required.Add(versionCheck);
            }

            var advisory = new List<PreflightCheck>
            {
                await CheckGhAuthAsync(ct).ConfigureAwait(false),
                CheckPolyphonyCli(),
                await CheckDotnetSdkAsync(ct).ConfigureAwait(false),
            };

            var failed = required.Count(c => !c.Passed);
            var warnings = advisory.Count(c => !c.Passed);
            var ready = failed == 0;

            var summary = (ready, warnings) switch
            {
                (true, 0) => "All preflight checks passed.",
                (true, _) => $"All required checks passed. {warnings} advisory warning(s).",
                _ => $"{failed} required check(s) failed. Fix before proceeding.",
            };

            result = new StatePreflightResult
            {
                Ready = ready,
                Summary = summary,
                RequiredChecks = required,
                AdvisoryChecks = advisory,
                FailedCount = failed,
                WarningCount = warnings,
                Details = new PreflightDetails
                {
                    WorkItemId = workItem,
                    AdoOrg = adoOrg,
                    AdoProject = adoProject,
                },
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new StatePreflightResult
            {
                Ready = false,
                Summary = $"Preflight check error: {ex.Message}",
                RequiredChecks = [],
                AdvisoryChecks = [],
                FailedCount = 1,
                WarningCount = 0,
                Details = new PreflightDetails
                {
                    WorkItemId = workItem,
                    Error = ex.Message,
                },
            };
        }

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StatePreflightResult));
        return ExitCodes.Success;
    }

    private async Task<PreflightCheck> CheckGitRepoAsync(CancellationToken ct)
    {
        try
        {
            var topLevel = await git.GetTopLevelAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(topLevel))
            {
                return new PreflightCheck
                {
                    Name = "git_repo",
                    Passed = true,
                    Detail = $"Git repository found at {topLevel}",
                };
            }
        }
        catch { /* fall through */ }
        return new PreflightCheck
        {
            Name = "git_repo",
            Passed = false,
            Detail = "Not inside a git repository",
            Remediation = "Run this workflow from within a git repository",
        };
    }

    private async Task<PreflightCheck> CheckTwigCliAsync(CancellationToken ct)
    {
        try
        {
            var version = await twig.GetVersionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(version))
            {
                return new PreflightCheck
                {
                    Name = "twig_cli",
                    Passed = true,
                    Detail = $"twig CLI available: {version}",
                };
            }
        }
        catch { /* fall through */ }
        return new PreflightCheck
        {
            Name = "twig_cli",
            Passed = false,
            Detail = "twig CLI not found or not responding",
            Remediation = "Install twig CLI and ensure it is in PATH",
        };
    }

    private async Task<(PreflightCheck Check, string? Org, string? Project)> CheckTwigConfigAsync(CancellationToken ct)
    {
        string? org = null, project = null;
        try
        {
            org = await twig.GetConfigValueAsync("organization", ct).ConfigureAwait(false);
            project = await twig.GetConfigValueAsync("project", ct).ConfigureAwait(false);
        }
        catch { /* fall through */ }

        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(project))
        {
            return (new PreflightCheck
            {
                Name = "twig_config",
                Passed = true,
                Detail = $"ADO workspace: {org}/{project}",
            }, org, project);
        }
        return (new PreflightCheck
        {
            Name = "twig_config",
            Passed = false,
            Detail = "twig config missing organization or project",
        }, org, project);
    }

    private async Task<PreflightCheck> CheckAdoAccessAsync(int workItem, CancellationToken ct)
    {
        try
        {
            var item = await twig.ShowAsync(workItem, ct).ConfigureAwait(false);
            if (item is not null)
            {
                var title = item["title"]?.GetValue<string>() ?? $"ID {workItem}";
                return new PreflightCheck
                {
                    Name = "ado_access",
                    Passed = true,
                    Detail = $"Work item accessible: {title}",
                };
            }
        }
        catch { /* fall through */ }
        return new PreflightCheck
        {
            Name = "ado_access",
            Passed = false,
            Detail = $"Cannot access work item {workItem}",
        };
    }

    private async Task<PreflightCheck> CheckGhAuthAsync(CancellationToken ct)
    {
        try
        {
            var status = await gh.GetAuthStatusAsync(ct).ConfigureAwait(false);
            if (status.IsAuthenticated)
            {
                return new PreflightCheck
                {
                    Name = "gh_auth",
                    Passed = true,
                    Detail = "GitHub CLI authenticated",
                };
            }
        }
        catch { /* fall through */ }
        return new PreflightCheck
        {
            Name = "gh_auth",
            Passed = false,
            Detail = "GitHub CLI not authenticated",
            Remediation = "Run: gh auth login",
        };
    }

    private static PreflightCheck CheckPolyphonyCli()
    {
        // We ARE polyphony — the binary is by definition available.
        // Read assembly version to preserve the version-detail contract.
        var version = PolyphonyVersion.GetCurrent();
        return new PreflightCheck
        {
            Name = "polyphony_cli",
            Passed = true,
            Detail = $"Polyphony CLI available: {version}",
        };
    }

    /// <summary>
    /// Resolve the required minimum polyphony version from
    /// <paramref name="explicitRequired"/> (testing seam, wins) or from
    /// <paramref name="workflowYaml"/>'s
    /// <c>workflow.metadata.min_polyphony_version</c> field. Returns
    /// <c>null</c> when neither source supplies a requirement (the check
    /// is opt-in — workflows without metadata don't get the check).
    /// </summary>
    /// <remarks>
    /// Failures that suggest configuration errors (e.g. unreadable YAML,
    /// unparsable required version) emit a failing
    /// <c>polyphony_version</c> check rather than throwing. That keeps
    /// the JSON-on-stdout contract intact and gives the gate a clear
    /// remediation surface.
    /// </remarks>
    private static PreflightCheck? TryCheckPolyphonyVersion(
        string? workflowYaml, string? explicitRequired)
    {
        string? required = explicitRequired;
        if (string.IsNullOrWhiteSpace(required) && !string.IsNullOrWhiteSpace(workflowYaml))
        {
            try
            {
                required = PolyphonyVersion.ReadMinVersionFromWorkflow(workflowYaml);
            }
            catch (Exception ex)
            {
                return new PreflightCheck
                {
                    Name = "polyphony_version",
                    Passed = false,
                    Detail = $"Cannot parse workflow YAML '{workflowYaml}': {ex.Message}",
                    Remediation = "Verify the workflow YAML at the supplied --workflow-yaml path is valid.",
                };
            }
        }

        if (string.IsNullOrWhiteSpace(required))
        {
            // No requirement declared — opt-in semantics. Workflows on
            // older registry versions without metadata.min_polyphony_version
            // simply skip this check rather than failing.
            return null;
        }

        var current = PolyphonyVersion.GetCurrent();
        var ok = PolyphonyVersion.Satisfies(current, required, out var reason);
        return new PreflightCheck
        {
            Name = "polyphony_version",
            Passed = ok,
            Detail = ok
                ? $"polyphony {current} satisfies workflow requirement >= {required}"
                : reason ?? "Polyphony version does not satisfy workflow requirement.",
            Remediation = ok ? null
                : "Update polyphony CLI: see docs/decisions/versioning-strategy.md " +
                  "for the install path. Mid-run upgrades are not supported in v1.",
        };
    }

    private async Task<PreflightCheck> CheckDotnetSdkAsync(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(DotnetExe, ["--version"], ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                var version = result.Stdout.Trim();
                return new PreflightCheck
                {
                    Name = "dotnet_sdk",
                    Passed = true,
                    Detail = $"dotnet SDK {version}",
                };
            }
        }
        catch { /* fall through */ }
        return new PreflightCheck
        {
            Name = "dotnet_sdk",
            Passed = false,
            Detail = "dotnet SDK not found",
            Remediation = "Install .NET SDK: https://dot.net",
        };
    }
}
