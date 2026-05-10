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
/// Matching: first entry whose `command` equals the invoked program name
/// (basename, no extension) AND whose `args` is a prefix of the actual argv
/// wins. Specific entries should appear before general ones.
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

        var commandName = ResolveCommandName();
        AppendAudit(manifest.AuditLog, commandName, args);

        var match = FindMatch(manifest.Responses, commandName, args);
        if (match is null)
        {
            EmitError($"no manifest entry for: {commandName} {string.Join(' ', args)}");
            return NoMatchExitCode;
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

    internal static ManifestResponse? FindMatch(
        IReadOnlyList<ManifestResponse> responses,
        string commandName,
        IReadOnlyList<string> args)
    {
        foreach (var entry in responses)
        {
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
            for (var i = 0; i < entryArgs.Count; i++)
            {
                if (!string.Equals(entryArgs[i], args[i], StringComparison.Ordinal))
                {
                    prefixMatches = false;
                    break;
                }
            }

            if (prefixMatches)
            {
                return entry;
            }
        }

        return null;
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
