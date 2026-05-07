using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/worktree-manager.ps1</c> — the
/// per-item worktree spawner/teardown helper for the apex-driver
/// dispatch loop.
/// </summary>
/// <remarks>
/// The apex-driver fans work-items out across waves and dispatches each
/// item into a per-item git worktree so multiple lifecycle sub-workflows
/// can run in parallel. This script's envelope is the only contract
/// between the script and the workflow; tests pin both happy paths and
/// the error cases so a contract drift surfaces in CI.
///
/// Tests are skipped when <c>pwsh</c> is not on PATH (e.g., on a CI
/// runner without PowerShell 7).
/// </remarks>
public sealed class WorktreeManagerScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "worktree-manager.ps1"));

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
            $"worktree-manager.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task SpawnWithBadBaseBranch_ReturnsWorktreeAddFailedButExitsZero()
    {
        if (!PwshAvailable) return;

        // Per polyphony-workflow-author convention: helper scripts exit
        // 0 and surface failures via `error_code` so the workflow's
        // catch-all route can fire without halting the conductor run.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-Operation spawn -WorkItemId 99999998 -BaseBranch nonexistent_branch_xyz");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("operation").GetString().ShouldBe("spawn");
        root.GetProperty("work_item_id").GetInt32().ShouldBe(99999998);
        root.GetProperty("error_code").GetString().ShouldBe("worktree_add_failed");
        root.GetProperty("error_message").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TeardownNonexistentWorktree_IsIdempotentSuccess()
    {
        if (!PwshAvailable) return;

        // Teardown is idempotent: a worktree that does not exist is a
        // no-op success so apex-driver re-entry on resume cannot wedge
        // on a half-cleaned-up dispatch.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-Operation teardown -WorkItemId 99999999");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("operation").GetString().ShouldBe("teardown");
        root.GetProperty("error_code").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllKeys()
    {
        if (!PwshAvailable) return;

        // Workflow templates reference every envelope field on the
        // catch-all route, so all keys must always be present.
        var inputs = new[]
        {
            "-Operation spawn -WorkItemId 1 -BaseBranch nonexistent_xyz",
            "-Operation teardown -WorkItemId 1",
        };

        var requiredKeys = new[]
        {
            "success", "operation", "work_item_id", "worktree_path",
            "branch", "error_code", "error_message",
        };

        foreach (var args in inputs)
        {
            var (exitCode, stdout, stderr) = await RunScriptAsync(args);
            exitCode.ShouldBe(0, $"args: {args} stderr: {stderr}");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            foreach (var key in requiredKeys)
            {
                root.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing for {args}");
            }
        }
    }

    [Fact]
    public async Task BranchName_IsApexConventional()
    {
        if (!PwshAvailable) return;

        // Branch name is an apex-driver contract: feature-pr.yaml and
        // wave-integrator.ps1 both rely on the sdlc/apex/<id> pattern.
        var (_, stdout, _) = await RunScriptAsync(
            "-Operation spawn -WorkItemId 42 -BaseBranch nonexistent_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("branch").GetString().ShouldBe("sdlc/apex/42");
    }
}
