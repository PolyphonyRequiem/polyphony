using System.Diagnostics;
using System.Reflection;
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

        // .NET runtime version check (require >= 7.0)
        var dotnetVersion = Environment.Version;
        var minDotnet = new Version(7, 0);
        bool dotnetOk = dotnetVersion >= minDotnet;
        checks.Add(new HealthCheckResult
        {
            Name = "dotnet-version",
            Success = dotnetOk,
            Message = dotnetOk ? $".NET version {dotnetVersion} OK" : $".NET {dotnetVersion} is below required {minDotnet}"
        });

        // Runtime AOT support
        bool aotSupported = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        checks.Add(new HealthCheckResult
        {
            Name = "aot-support",
            Success = aotSupported,
            Message = aotSupported ? "AOT supported" : "AOT not supported on this runtime"
        });

        // SQLite availability and WAL mode
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = cmd.ExecuteScalar()?.ToString() ?? "";
            bool wal = mode.Equals("wal", StringComparison.OrdinalIgnoreCase);
            checks.Add(new HealthCheckResult
            {
                Name = "sqlite-wal",
                Success = wal,
                Message = wal ? "SQLite WAL mode enabled" : $"SQLite journal_mode is '{mode}', expected 'wal'"
            });
        }
        catch (Exception ex)
        {
            checks.Add(new HealthCheckResult
            {
                Name = "sqlite",
                Success = false,
                Message = $"SQLite unavailable: {ex.Message}"
            });
        }

        // YamlDotNet compatibility
        try
        {
            var yaml = "a: 1\nb: 2";
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var obj = deserializer.Deserialize<Dictionary<string, int>>(yaml);
            bool ok = obj != null && obj.Count == 2 && obj["a"] == 1 && obj["b"] == 2;
            checks.Add(new HealthCheckResult
            {
                Name = "yamldotnet",
                Success = ok,
                Message = ok ? "YamlDotNet basic parse OK" : "YamlDotNet parse failed"
            });
        }
        catch (Exception ex)
        {
            checks.Add(new HealthCheckResult
            {
                Name = "yamldotnet",
                Success = false,
                Message = $"YamlDotNet error: {ex.Message}"
            });
        }

        // OS/arch/dotnet/polyphony version
        var result = new HealthResult
        {
            Checks = checks.ToArray(),
            Os = Environment.OSVersion.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            DotnetVersion = dotnetVersion.ToString(),
            PolyphonyVersion = ResolvePolyphonyVersion(),
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HealthResult));
        return result.AllCriticalPassed ? ExitCodes.Success : ExitCodes.HealthCheckFailed;
    }

    // Read AssemblyInformationalVersion (where MinVer writes the real SemVer,
    // including pre-release/build-metadata). Falls back to the numeric
    // AssemblyVersion only as a last resort — that field is set to a stable
    // 1.0.0.0 by MinVer and would mask the real version on every release.
    private static string ResolvePolyphonyVersion()
    {
        var asm = typeof(HealthCommand).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
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
