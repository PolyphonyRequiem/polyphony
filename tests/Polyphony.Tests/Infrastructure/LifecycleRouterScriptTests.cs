using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/lifecycle-router.ps1</c> — the
/// per-item lifecycle classifier for the apex-driver dispatch loop.
/// </summary>
/// <remarks>
/// The apex-driver dispatches each work-item into one of five lifecycle
/// workflows (plan-level, actionable, implement-pg, feature-pr,
/// fast-path) plus three non-dispatch outcomes (monitoring, blocked,
/// error). This script is the deterministic classifier — its envelope
/// is the workflow's input schema for the routing step.
///
/// Tests focus on the envelope shape and the polyphony-unavailable
/// failure mode. Live <c>polyphony state next-ready</c> integration is
/// covered by the verb's own xUnit suite; this script's mapping logic
/// is exercised end-to-end by the Phase 7 e2e PR (forward reference).
/// </remarks>
public sealed class LifecycleRouterScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "lifecycle-router.ps1"));

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
            $"lifecycle-router.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task PolyphonyMissing_PopulatesPolyphonyUnavailableButExitsZero()
    {
        if (!PwshAvailable) return;

        // Per polyphony-workflow-author convention: routing scripts
        // exit 0 even on hard failures and surface them via error_code
        // so the workflow's catch-all route fires.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-WorkItemId 1 -ApexId 1 -PolyphonyExe nonexistent_polyphony_xyz");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("error_code").GetString().ShouldBe("polyphony_unavailable");
        root.GetProperty("lifecycle_workflow").GetString().ShouldBe("error");
        root.GetProperty("status").GetString().ShouldBe("error");
    }

    [Fact]
    public async Task IsRoot_TrueWhenWorkItemEqualsApex()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-WorkItemId 42 -ApexId 42 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("is_root").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task IsRoot_FalseWhenWorkItemDiffersFromApex()
    {
        if (!PwshAvailable) return;

        var (_, stdout, _) = await RunScriptAsync(
            "-WorkItemId 7 -ApexId 42 -PolyphonyExe nonexistent_polyphony_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("is_root").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllKeys()
    {
        if (!PwshAvailable) return;

        // Workflow templates reference every envelope field on the
        // catch-all route, so all keys must always be present.
        var requiredKeys = new[]
        {
            "success", "work_item_id", "work_item_type", "status",
            "lifecycle_workflow", "next_kinds", "fulfilling_kinds",
            "is_root", "error_code", "error_message",
        };

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            "-WorkItemId 1 -ApexId 1 -PolyphonyExe nonexistent_polyphony_xyz");
        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        foreach (var key in requiredKeys)
        {
            doc.RootElement.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing");
        }
    }
}
