using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/batch-integrator.ps1</c> — the
/// post-batch branch integrator for the polyphony dispatch loop.
/// </summary>
/// <remarks>
/// After a batch completes, the polyphony invokes this script to merge
/// each completed child branch (sdlc/root/&lt;id&gt;) back into the root
/// feature branch in topological order. This script's envelope is the
/// workflow's input schema for the batch_failed_gate routing step.
///
/// Tests focus on envelope shape and surface-level error paths
/// (polyphony missing, edges check failure). Live integration with
/// real branches is covered by the Phase 7 e2e PR (forward reference).
/// </remarks>
[Trait("Category", "Slow")] // see #286 — forks pwsh per test
public sealed class BatchIntegratorScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "batch-integrator.ps1"));

    private static bool PwshAvailable
    {
        get
        {
            try
            {
                using var probe = new Process();
                probe.StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    Arguments = "-NoProfile -Command \"$PSVersionTable.PSVersion.Major\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                probe.Start();
                probe.WaitForExit();
                return probe.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunScriptAsync(string args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -File \"{ScriptPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void ScriptFile_Exists()
    {
        File.Exists(ScriptPath).ShouldBeTrue(
            $"batch-integrator.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task PolyphonyMissing_PopulatesPolyphonyUnavailableButExitsZero()
    {
        if (!PwshAvailable) return;

        var (exitCode, stdout, _) = await RunScriptAsync(
            "-RootId 1 -BatchIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("error_code").GetString().ShouldBe("polyphony_unavailable");
        root.GetProperty("root_id").GetInt32().ShouldBe(1);
        root.GetProperty("batch_index").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task FeatureBranch_DefaultsToRootConvention()
    {
        if (!PwshAvailable) return;

        // The default feature branch is feature/<RootId> per the
        // branch-model spec; this is an polyphony contract that
        // worktree-manager.ps1 + the workflow itself rely on. Pre-PR-176
        // the script defaulted to feature/root-<RootId>; that root-
        // sub-prefix was a YAML/script drift bypassing BranchNameBuilder.
        var (_, stdout, _) = await RunScriptAsync(
            "-RootId 9876 -BatchIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("feature_branch").GetString().ShouldBe("feature/9876");
    }

    [Fact]
    public async Task FeatureBranch_OverrideRespected()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-RootId 1 -BatchIndex 0 -FeatureBranch custom/feature -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("feature_branch").GetString().ShouldBe("custom/feature");
    }

    [Fact]
    public async Task DefaultMergeStrategy_IsNoFf()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-RootId 1 -BatchIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("merge_strategy").GetString().ShouldBe("no-ff");
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllKeys()
    {
        if (!PwshAvailable) return;

        var requiredKeys = new[]
        {
            "success", "batch_index", "root_id", "feature_branch",
            "merge_strategy", "branches_integrated", "skipped",
            "conflicts", "error_code", "error_message",
        };

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            "-RootId 1 -BatchIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");
        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        foreach (var key in requiredKeys)
        {
            doc.RootElement.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing");
        }
    }
}
