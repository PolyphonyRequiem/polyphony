using System.Text.Json;

namespace Polyphony.HarnessShim;

/// <summary>
/// Test-time shim that impersonates polyphony / twig / gh for harness scenarios.
///
/// The harness driver places copies of this binary on a per-scenario PATH so
/// conductor's script: nodes resolve to the shim instead of the real CLI.
/// The shim looks up a scripted response in a JSON manifest pointed at by
/// POLYPHONY_HARNESS_MANIFEST and replays its stdout / stderr / exit code.
///
/// Matching: walks the manifest in order; the first entry whose <c>command</c>
/// equals the invoked program name (basename, no extension), whose <c>args</c>
/// is a prefix of the actual argv, and whose consumption count has not yet
/// reached its <c>times</c> cap (if any) wins. Specific entries should appear
/// before general ones.
///
/// Per-call sequencing is expressed by listing the same (command, args)
/// matcher multiple times with <c>times: 1</c> on each — the first selection
/// burns the first entry, the next selection burns the second, etc. Counter
/// state is persisted in a sibling <c>&lt;manifest&gt;.counters.json</c> file
/// across shim invocations.
///
/// No match → exit 99 with a structured JSON error on stderr so authors
/// notice missing manifest entries immediately.
/// </summary>
internal static class Program
{
    private const int NoMatchExitCode = 99;
    private const int ManifestErrorExitCode = 98;

    private static int Main(string[] args)
    {
        var manifestPath = Environment.GetEnvironmentVariable("POLYPHONY_HARNESS_MANIFEST");
        if (string.IsNullOrEmpty(manifestPath))
        {
            EmitError("missing POLYPHONY_HARNESS_MANIFEST environment variable");
            return ManifestErrorExitCode;
        }

        if (!File.Exists(manifestPath))
        {
            EmitError($"manifest not found at '{manifestPath}'");
            return ManifestErrorExitCode;
        }

        Manifest manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, ShimJsonContext.Default.Manifest)
                       ?? throw new InvalidOperationException("manifest deserialized as null");
        }
        catch (Exception ex)
        {
            EmitError($"failed to read manifest at '{manifestPath}': {ex.Message}");
            return ManifestErrorExitCode;
        }

        var counterPath = CounterPathForManifest(manifestPath);
        var counters = LoadCounters(counterPath);

        var commandName = ResolveCommandName();
        AppendAudit(manifest.AuditLog, commandName, args);

        var (match, matchIndex) = FindMatchWithIndex(manifest.Responses, commandName, args, counters);
        if (match is null)
        {
            EmitError($"no manifest entry for: {commandName} {string.Join(' ', args)}");
            return NoMatchExitCode;
        }

        if (match.Times.HasValue)
        {
            var key = matchIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            counters[key] = counters.GetValueOrDefault(key, 0) + 1;
            SaveCounters(counterPath, counters);
        }

        if (!string.IsNullOrEmpty(match.Stdout))
        {
            Console.Out.Write(match.Stdout);
            if (!match.Stdout.EndsWith('\n'))
            {
                Console.Out.Write('\n');
            }
        }

        if (!string.IsNullOrEmpty(match.Stderr))
        {
            Console.Error.Write(match.Stderr);
            if (!match.Stderr.EndsWith('\n'))
            {
                Console.Error.Write('\n');
            }
        }

        return match.ExitCode;
    }

    /// <summary>
    /// Argv[0] is the path the OS resolved for the executable. Strip directory
    /// and extension so a copy named polyphony.exe is reported as "polyphony"
    /// regardless of platform.
    /// </summary>
    internal static string ResolveCommandName()
    {
        var argv0 = Environment.GetCommandLineArgs().FirstOrDefault() ?? "shim";
        return Path.GetFileNameWithoutExtension(argv0);
    }

    /// <summary>
    /// Legacy first-match-wins matcher with no consumption tracking. Retained
    /// for the existing xUnit tests that exercise prefix-matching semantics
    /// directly without needing a counters dictionary.
    /// </summary>
    internal static ManifestResponse? FindMatch(
        IReadOnlyList<ManifestResponse> responses,
        string commandName,
        IReadOnlyList<string> args)
    {
        var (match, _) = FindMatchWithIndex(responses, commandName, args, counters: null);
        return match;
    }

    /// <summary>
    /// First-match-wins with optional per-entry consumption caps. Walks
    /// <paramref name="responses"/> in order; an entry matches when its
    /// command equals <paramref name="commandName"/>, its args are a prefix
    /// of <paramref name="args"/>, AND (when <c>times</c> is set) its
    /// counter has not yet reached <c>times</c>. Returns the matched entry
    /// and its index into <paramref name="responses"/>.
    /// </summary>
    internal static (ManifestResponse? Entry, int Index) FindMatchWithIndex(
        IReadOnlyList<ManifestResponse> responses,
        string commandName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, int>? counters)
    {
        for (var i = 0; i < responses.Count; i++)
        {
            var entry = responses[i];

            if (!string.Equals(entry.Command, commandName, StringComparison.Ordinal))
            {
                continue;
            }

            var entryArgs = entry.Args ?? new List<string>();
            if (entryArgs.Count > args.Count)
            {
                continue;
            }

            var prefixMatches = true;
            for (var j = 0; j < entryArgs.Count; j++)
            {
                if (!string.Equals(entryArgs[j], args[j], StringComparison.Ordinal))
                {
                    prefixMatches = false;
                    break;
                }
            }

            if (!prefixMatches)
            {
                continue;
            }

            if (entry.Times is int cap)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var used = counters is not null && counters.TryGetValue(key, out var c) ? c : 0;
                if (used >= cap)
                {
                    continue;
                }
            }

            return (entry, i);
        }

        return (null, -1);
    }

    internal static string CounterPathForManifest(string manifestPath)
    {
        return manifestPath + ".counters.json";
    }

    internal static Dictionary<string, int> LoadCounters(string counterPath)
    {
        if (!File.Exists(counterPath))
        {
            return new Dictionary<string, int>();
        }

        try
        {
            using var stream = File.OpenRead(counterPath);
            var state = JsonSerializer.Deserialize(stream, ShimJsonContext.Default.CounterState);
            return state?.Counters ?? new Dictionary<string, int>();
        }
        catch
        {
            // A malformed counter file shouldn't crash the shim — treat as
            // empty so the scenario runs cleanly and any sequencing bug
            // surfaces as a stable off-by-one in the audit log.
            return new Dictionary<string, int>();
        }
    }

    internal static void SaveCounters(string counterPath, IReadOnlyDictionary<string, int> counters)
    {
        var state = new CounterState
        {
            Counters = new Dictionary<string, int>(counters),
        };
        var json = JsonSerializer.Serialize(state, ShimJsonContext.Default.CounterState);
        File.WriteAllText(counterPath, json);
    }

    private static void AppendAudit(string? auditLogPath, string commandName, IReadOnlyList<string> args)
    {
        if (string.IsNullOrEmpty(auditLogPath))
        {
            return;
        }

        try
        {
            var line = $"{DateTime.UtcNow:O}\t{commandName}\t{string.Join(' ', args)}{Environment.NewLine}";
            File.AppendAllText(auditLogPath, line);
        }
        catch
        {
            // Audit failures must never break a scenario.
        }
    }

    private static void EmitError(string message)
    {
        var payload = new ShimError
        {
            Error = message,
            Argv = Environment.GetCommandLineArgs(),
        };
        var json = JsonSerializer.Serialize(payload, ShimJsonContext.Default.ShimError);
        Console.Error.WriteLine(json);
    }
}
