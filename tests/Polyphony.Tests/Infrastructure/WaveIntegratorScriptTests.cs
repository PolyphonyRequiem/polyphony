using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/wave-integrator.ps1</c> — the
/// post-wave branch integrator for the apex-driver dispatch loop.
/// </summary>
/// <remarks>
/// After a wave completes, the apex-driver invokes this script to merge
/// each completed child branch (sdlc/apex/&lt;id&gt;) back into the apex
/// feature branch in topological order. This script's envelope is the
/// workflow's input schema for the wave_failed_gate routing step.
///
/// Tests focus on envelope shape and surface-level error paths
/// (polyphony missing, edges check failure). Live integration with
/// real branches is covered by the Phase 7 e2e PR (forward reference).
/// </remarks>
public sealed class WaveIntegratorScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "wave-integrator.ps1"));

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
            $"wave-integrator.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task PolyphonyMissing_PopulatesPolyphonyUnavailableButExitsZero()
    {
        if (!PwshAvailable) return;

        var (exitCode, stdout, _) = await RunScriptAsync(
            "-ApexId 1 -WaveIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("error_code").GetString().ShouldBe("polyphony_unavailable");
        root.GetProperty("apex_id").GetInt32().ShouldBe(1);
        root.GetProperty("wave_index").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task FeatureBranch_DefaultsToApexConvention()
    {
        if (!PwshAvailable) return;

        // The default feature branch is feature/apex-<ApexId>; this is
        // an apex-driver contract that worktree-manager.ps1 + the
        // workflow itself rely on.
        var (_, stdout, _) = await RunScriptAsync(
            "-ApexId 9876 -WaveIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("feature_branch").GetString().ShouldBe("feature/apex-9876");
    }

    [Fact]
    public async Task FeatureBranch_OverrideRespected()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-ApexId 1 -WaveIndex 0 -FeatureBranch custom/feature -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("feature_branch").GetString().ShouldBe("custom/feature");
    }

    [Fact]
    public async Task DefaultMergeStrategy_IsNoFf()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-ApexId 1 -WaveIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("merge_strategy").GetString().ShouldBe("no-ff");
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllKeys()
    {
        if (!PwshAvailable) return;

        var requiredKeys = new[]
        {
            "success", "wave_index", "apex_id", "feature_branch",
            "merge_strategy", "branches_integrated", "skipped",
            "conflicts", "error_code", "error_message",
        };

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            "-ApexId 1 -WaveIndex 0 -PolyphonyExe nonexistent_polyphony_xyz");
        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        foreach (var key in requiredKeys)
        {
            doc.RootElement.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing");
        }
    }
}
