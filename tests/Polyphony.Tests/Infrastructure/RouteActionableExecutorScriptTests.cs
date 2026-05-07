using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/route-actionable-executor.ps1</c> — the
/// executor router for the actionable.yaml conductor workflow.
/// </summary>
/// <remarks>
/// The actionable workflow has two legs (polyphony / human) and routes
/// between them on this script's output. The envelope is the only
/// contract between the script and the workflow; tests pin both the
/// happy paths and the unknown-executor error case so a contract drift
/// surfaces in CI rather than at run time.
/// Tests are skipped when <c>pwsh</c> is not on PATH (e.g., on a CI
/// runner without PowerShell 7).
/// </remarks>
public sealed class RouteActionableExecutorScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "route-actionable-executor.ps1"));

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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunScriptAsync(
        string args)
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
            $"route-actionable-executor.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task PolyphonyExecutor_EmitsExpectedEnvelope()
    {
        if (!PwshAvailable) return;

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            "-WorkItemId 12345 -Executor polyphony");

        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("executor").GetString().ShouldBe("polyphony");
        root.GetProperty("work_item_id").GetInt32().ShouldBe(12345);
        root.GetProperty("error").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task HumanExecutor_EmitsExpectedEnvelope()
    {
        if (!PwshAvailable) return;

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            "-WorkItemId 67890 -Executor human");

        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("executor").GetString().ShouldBe("human");
        root.GetProperty("work_item_id").GetInt32().ShouldBe(67890);
        root.GetProperty("error").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DefaultExecutor_IsPolyphony()
    {
        if (!PwshAvailable) return;

        // The actionable workflow declares `executor` with default
        // 'polyphony'. The script must mirror that default so callers
        // who don't pass -Executor still produce a well-typed envelope.
        var (exitCode, stdout, _) = await RunScriptAsync("-WorkItemId 1");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("executor").GetString().ShouldBe("polyphony");
        doc.RootElement.GetProperty("error").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task UnknownExecutor_PopulatesErrorButStillExitsZero()
    {
        if (!PwshAvailable) return;

        // Per polyphony-workflow-author convention: routing scripts
        // exit 0 and surface failures via `error` so the workflow's
        // catch-all route can fire. A non-zero exit would halt the
        // conductor run before the route resolves.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-WorkItemId 42 -Executor robot");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("work_item_id").GetInt32().ShouldBe(42);
        var error = root.GetProperty("error").GetString();
        error.ShouldNotBeNullOrEmpty();
        error.ShouldContain("polyphony");
        error.ShouldContain("human");
        error.ShouldContain("robot");
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllThreeKeys()
    {
        if (!PwshAvailable) return;

        // Workflow templates reference `executor_router.output.executor`,
        // `.work_item_id`, and `.error` unconditionally on the
        // catch-all route, so all three keys must always be present.
        var inputs = new[]
        {
            "-WorkItemId 1 -Executor polyphony",
            "-WorkItemId 2 -Executor human",
            "-WorkItemId 3 -Executor invalid",
            "-WorkItemId 4",
        };

        foreach (var args in inputs)
        {
            var (exitCode, stdout, stderr) = await RunScriptAsync(args);
            exitCode.ShouldBe(0, $"args: {args} stderr: {stderr}");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            root.TryGetProperty("executor", out _).ShouldBeTrue($"executor missing for {args}");
            root.TryGetProperty("work_item_id", out _).ShouldBeTrue($"work_item_id missing for {args}");
            root.TryGetProperty("error", out _).ShouldBeTrue($"error missing for {args}");
        }
    }
}
