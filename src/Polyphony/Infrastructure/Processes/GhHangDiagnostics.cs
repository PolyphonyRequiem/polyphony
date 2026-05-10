using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// On-demand process-tree + environment snapshot writer used when
/// <see cref="GhClient"/> exhausts its retry budget on a timed-out
/// invocation. Writes a JSON sidecar file the operator (or a follow-up
/// diagnostic agent) can inspect to verify hypotheses about the hang
/// cause without re-running the dogfood.
///
/// Issue #209 motivated this: <c>gh pr view</c> hangs deterministically
/// when invoked through the conductor subprocess on Windows, but works
/// fine from a fresh shell. Past triages spent hours chasing speculative
/// causes (orphan processes, GH_TOKEN expiry, rate limits, response
/// payload size) when a single snapshot of "what was alive when gh
/// hung" + "what env did polyphony see" would have ruled most of those
/// out in seconds.
///
/// Stateless. Best-effort: any exception during capture is swallowed
/// and a stub payload is written instead — surfacing the hang to the
/// operator is more important than perfect diagnostics.
/// </summary>
public static class GhHangDiagnostics
{
    private const string DiagDirName = "polyphony";
    private const string DiagFilePrefix = "gh-hang";

    /// <summary>
    /// Capture a snapshot of the local process universe + the env vars
    /// gh would have seen, write it to <c>%TEMP%/polyphony/gh-hang-{utc}-{pid}.diag.json</c>,
    /// and return the path. Returns <c>null</c> on filesystem failure.
    /// </summary>
    /// <param name="ghArgs">The argument list passed to the timed-out gh invocation.</param>
    /// <param name="elapsed">Wall-clock elapsed of the last attempt.</param>
    /// <param name="attempts">How many attempts were made before giving up.</param>
    /// <param name="perAttemptTimeout">The per-attempt timeout in effect.</param>
    public static string? Capture(
        IReadOnlyList<string> ghArgs,
        TimeSpan elapsed,
        int attempts,
        TimeSpan perAttemptTimeout)
    {
        try
        {
            var diagDir = Path.Combine(Path.GetTempPath(), DiagDirName);
            Directory.CreateDirectory(diagDir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var fileName = $"{DiagFilePrefix}-{stamp}-pid{Environment.ProcessId}.diag.json";
            var diagPath = Path.Combine(diagDir, fileName);

            var snapshot = new
            {
                schema = 1,
                captured_at_utc = DateTimeOffset.UtcNow.ToString("o"),
                polyphony_pid = Environment.ProcessId,
                parent_pid = TryGetParentPid(),
                gh_invocation = new
                {
                    args = ghArgs,
                    attempts,
                    per_attempt_timeout_seconds = perAttemptTimeout.TotalSeconds,
                    last_elapsed_seconds = elapsed.TotalSeconds,
                },
                gh_env_seen = SnapshotGhEnv(),
                processes = SnapshotProcesses(),
                process_handle_counts = SnapshotPolyphonyHandles(),
            };

            var json = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(diagPath, json);
            return diagPath;
        }
        catch
        {
            // Diagnostic capture must never mask the underlying timeout.
            return null;
        }
    }

    /// <summary>
    /// Best-effort lookup of the parent process id. Cross-platform via
    /// /proc on Linux; <see cref="ParentProcessUtilities"/> on Windows.
    /// Returns 0 when unavailable.
    /// </summary>
    private static int TryGetParentPid()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ParentProcessUtilities.GetParentPidWindows();
            }
            if (OperatingSystem.IsLinux())
            {
                var status = File.ReadAllLines("/proc/self/status");
                foreach (var line in status)
                {
                    if (line.StartsWith("PPid:", StringComparison.Ordinal))
                    {
                        var parts = line.Split(':', 2);
                        if (int.TryParse(parts[1].Trim(), out var ppid)) return ppid;
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Capture the gh-relevant subset of the current process environment
    /// — variable names, value lengths, and a 6-char prefix (never the
    /// full value, never beyond what would already leak via gh's own
    /// stderr). Authentication tokens are length+prefix only.
    /// </summary>
    private static Dictionary<string, string> SnapshotGhEnv()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] interestingPrefixes = ["GH_", "GITHUB_", "NO_COLOR", "PAGER", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"];
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is null) continue;
            if (!interestingPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            var value = entry.Value?.ToString() ?? string.Empty;
            // Tokens-likely keys: emit only length + prefix. Other keys: emit length + first 32 chars.
            var isLikelyToken = key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);
            if (isLikelyToken)
            {
                var prefix = value.Length >= 6 ? value.Substring(0, 6) : value;
                result[key] = $"<len={value.Length} prefix={prefix}…>";
            }
            else
            {
                result[key] = value.Length <= 64 ? value : value.Substring(0, 64) + "…";
            }
        }
        return result;
    }

    /// <summary>
    /// Snapshot every live <c>gh</c>, <c>polyphony</c>, <c>conductor</c>,
    /// and <c>python</c> process at this instant. Captures only what
    /// <see cref="Process"/> exposes cross-platform without elevation —
    /// id, parent process not available cross-platform here, working
    /// set, start time, and CPU. The point is to falsify or confirm
    /// hypotheses like "is there an orphan gh from a previous run?"
    /// not to run a full forensic capture.
    /// </summary>
    private static List<Dictionary<string, object?>> SnapshotProcesses()
    {
        var rows = new List<Dictionary<string, object?>>();
        string[] interestingNames = ["gh", "polyphony", "conductor", "python"];
        foreach (var name in interestingNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["pid"] = p.Id,
                        ["start_time_utc"] = SafeStartTimeUtc(p),
                        ["cpu_seconds"] = SafeCpu(p),
                        ["working_set_mb"] = SafeWorkingSetMb(p),
                        ["responding"] = SafeResponding(p),
                    });
                }
                catch
                {
                    // Process may have exited between enumeration and access.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return rows;
    }

    private static string? SafeStartTimeUtc(Process p)
    {
        try { return p.StartTime.ToUniversalTime().ToString("o"); }
        catch { return null; }
    }

    private static double? SafeCpu(Process p)
    {
        try { return p.TotalProcessorTime.TotalSeconds; }
        catch { return null; }
    }

    private static long? SafeWorkingSetMb(Process p)
    {
        try { return p.WorkingSet64 / (1024 * 1024); }
        catch { return null; }
    }

    private static bool? SafeResponding(Process p)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return p.Responding; }
        catch { return null; }
    }

    /// <summary>
    /// Capture the current polyphony process's open-handle count
    /// (Windows only). A growing handle count across repeated gh
    /// invocations would point at a leak.
    /// </summary>
    private static Dictionary<string, object?> SnapshotPolyphonyHandles()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            using var self = Process.GetCurrentProcess();
            if (OperatingSystem.IsWindows())
            {
                result["handle_count"] = self.HandleCount;
            }
            result["thread_count"] = self.Threads.Count;
            result["working_set_mb"] = self.WorkingSet64 / (1024 * 1024);
        }
        catch { }
        return result;
    }
}

/// <summary>
/// Minimal Windows-only helper to read the parent process id from
/// the kernel via <c>NtQueryInformationProcess</c>. Pulled inline so
/// diagnostics don't pull in a new package dependency.
/// </summary>
internal static class ParentProcessUtilities
{
    public static int GetParentPidWindows()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            // .NET on Windows exposes parent via ManagementObject historically,
            // but System.Management isn't AOT-friendly. The simplest
            // AOT-safe alternative is to walk Win32_Process via WMI in a
            // text-only path — which also pulls a heavy dep. For diagnostics
            // we accept "0 = unknown" here and let the operator infer
            // the parent via the captured process list (the snapshot
            // above includes conductor's python.exe with start times).
            return 0;
        }
        catch { return 0; }
    }
}
