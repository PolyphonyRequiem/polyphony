using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Commands;

/// <summary>
/// Performs environment and configuration diagnostics for Polyphony CLI.
/// </summary>
[VerbGroup("")]
public sealed class HealthCommand
{
    private readonly Func<string, HealthCheckResult> _checkTool;
    private readonly IAdoClient? _adoClient;

    // Single ctor (ConsoleAppFramework CAF011 — Add<T> rejects multiple ctors).
    // Both injected dependencies are optional with sensible defaults so:
    //   - production runs get the DI-resolved IAdoClient (registered in
    //     PolyphonyServiceRegistration) and the default tool-checker.
    //   - unit tests can pass null for adoClient (skipping the network probe)
    //     and a stub toolChecker so they don't depend on `twig` / `git` /
    //     dev.azure.com being reachable in CI.
    public HealthCommand(
        Func<string, HealthCheckResult>? toolChecker = null,
        IAdoClient? adoClient = null)
    {
        _checkTool = toolChecker ?? DefaultCheckTool;
        _adoClient = adoClient;
    }

    [Command("health")]
    [VerbResult(typeof(HealthResult))]
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

        // Azure DevOps auth probe (best-effort — network call, not on the
        // critical-checks list). Skipped entirely when no IAdoClient is
        // injected (e.g. in unit tests).
        if (_adoClient is not null)
        {
            try
            {
                // GetAuthStatusAsync never throws; it always returns a status.
                // Bound the call so a wedged dev.azure.com cannot stall a
                // health check. The client also enforces its own timeout, but
                // belt-and-braces here is cheap.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var status = _adoClient.GetAuthStatusAsync(cts.Token).GetAwaiter().GetResult();
                checks.Add(new HealthCheckResult
                {
                    Name = "ado",
                    Success = status.IsAuthenticated,
                    Message = status.Detail,
                });
            }
            catch (Exception ex)
            {
                checks.Add(new HealthCheckResult
                {
                    Name = "ado",
                    Success = false,
                    Message = $"ADO probe threw: {ex.Message}",
                });
            }
        }

        // OS/arch/dotnet/polyphony version
        var result = new HealthResult
        {
            Checks = checks.ToArray(),
            Os = Environment.OSVersion.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            DotnetVersion = dotnetVersion.ToString(),
            PolyphonyVersion = ResolvePolyphonyVersion(),
            CanonicalWorkflow = CanonicalWorkflowRef,
        };

        // Breadcrumb to STDERR so a first-time user sees the SDLC entry point
        // without polluting the STDOUT JSON contract that `JsonOutputContractTests`
        // and any caller-script parsing relies on.
        Console.Error.WriteLine($"Canonical SDLC entry point: conductor run {CanonicalWorkflowRef} --input apex_id=<ID>");

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HealthResult));
        return result.AllCriticalPassed ? ExitCodes.Success : ExitCodes.HealthCheckFailed;
    }

    // Canonical SDLC entry-point reference (workflow_name@process_template) emitted
    // both in the JSON `canonical_workflow` field and the STDERR breadcrumb. Hardcoded
    // — the truth lives in .conductor/registry/index.yaml, but parsing the registry
    // from a diagnostic verb would couple HealthCommand to the registry loader.
    private const string CanonicalWorkflowRef = "apex-driver@polyphony";

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
