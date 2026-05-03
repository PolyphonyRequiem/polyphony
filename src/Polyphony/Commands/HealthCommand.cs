using System.Diagnostics;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;

namespace Polyphony.Commands;

/// <summary>
/// Performs environment and configuration diagnostics for Polyphony CLI.
/// </summary>
public sealed class HealthCommand
{
    private readonly Func<string, HealthCheckResult> _checkTool;

    // Single ctor (ConsoleAppFramework CAF011 — Add<T> rejects multiple ctors).
    // The optional `toolChecker` is a test seam: production calls pass nothing
    // (DI does not register `Func<string, HealthCheckResult>`, so the default
    // null falls through to `DefaultCheckTool`), and unit tests can inject a
    // healthy stub so they don't depend on `twig` / `git` being on PATH in CI.
    public HealthCommand(Func<string, HealthCheckResult>? toolChecker = null)
    {
        _checkTool = toolChecker ?? DefaultCheckTool;
    }

    [Command("health")]
    public int Health(string config = ".conductor/process-config.yaml")
    {
        var checks = new List<HealthCheckResult>();

        // Check process-config.yaml
        if (!File.Exists(config))
        {
            checks.Add(new HealthCheckResult
            {
                Name = "process-config",
                Success = false,
                Message = $"Config file not found: {config}"
            });
        }
        else
        {
            try
            {
                var _ = ProcessConfigLoader.Load(config);
                checks.Add(new HealthCheckResult
                {
                    Name = "process-config",
                    Success = true,
                    Message = "Loaded successfully"
                });
            }
            catch (Exception ex)
            {
                checks.Add(new HealthCheckResult
                {
                    Name = "process-config",
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // Check twig on PATH
        checks.Add(_checkTool("twig"));
        // Check git on PATH
        checks.Add(_checkTool("git"));

        // OS/arch/dotnet/polyphony version
        var result = new HealthResult
        {
            Checks = checks.ToArray(),
            Os = Environment.OSVersion.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            DotnetVersion = Environment.Version.ToString(),
            PolyphonyVersion = typeof(HealthCommand).Assembly.GetName().Version?.ToString() ?? "",
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HealthResult));
        return result.AllCriticalPassed ? ExitCodes.Success : ExitCodes.HealthCheckFailed;
    }

    private static HealthCheckResult DefaultCheckTool(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return new HealthCheckResult { Name = tool, Success = false, Message = "Failed to start process" };
            proc.WaitForExit(3000);
            var output = proc.StandardOutput.ReadToEnd().Trim();
            var error = proc.StandardError.ReadToEnd().Trim();
            if (proc.ExitCode == 0)
                return new HealthCheckResult { Name = tool, Success = true, Message = output };
            return new HealthCheckResult { Name = tool, Success = false, Message = error != "" ? error : output };
        }
        catch (Exception ex)
        {
            return new HealthCheckResult { Name = tool, Success = false, Message = ex.Message };
        }
    }
}
