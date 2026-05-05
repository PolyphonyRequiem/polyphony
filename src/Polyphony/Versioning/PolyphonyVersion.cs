using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Polyphony.Versioning;

/// <summary>
/// Helpers for reading the running polyphony CLI version, parsing
/// <c>workflow.metadata.min_polyphony_version</c> from a workflow YAML
/// file, and comparing two SemVer strings.
/// </summary>
/// <remarks>
/// <para>
/// Implements the bundled-SemVer enforcement model captured in
/// <c>docs/decisions/versioning-strategy.md</c>: every workflow YAML
/// declares the minimum CLI version it requires; preflight reads the
/// declaration and refuses to run when the running CLI is older.
/// </para>
/// <para>
/// SemVer comparison is intentionally <em>core-only</em>: the
/// <c>major.minor.patch</c> tuple is compared numerically; pre-release
/// suffixes (<c>-alpha.0.5</c>) and build metadata (<c>+sha</c>) are
/// stripped before comparing. This is a deliberate loosening from the
/// strict SemVer ordering ("1.0.0-alpha &lt; 1.0.0") because the only
/// thing the gate cares about is "does the CLI advertise at least the
/// required X.Y.Z?". Pre-release status of the running build is not a
/// meaningful gate signal in practice.
/// </para>
/// </remarks>
public static class PolyphonyVersion
{
    /// <summary>
    /// Read <c>AssemblyInformationalVersion</c> from the running
    /// polyphony binary. Falls back to the numeric AssemblyVersion if
    /// MinVer didn't stamp it (e.g. running from an unrestored build).
    /// </summary>
    public static string GetCurrent() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Parse the SemVer core (major.minor.patch) from a string,
    /// stripping pre-release (<c>-foo</c>) and build metadata
    /// (<c>+bar</c>) suffixes per SemVer 2.0.0.
    /// </summary>
    /// <returns>
    /// The (major, minor, patch) tuple, or <c>null</c> if the input
    /// does not start with a recognizable SemVer core.
    /// </returns>
    public static (int Major, int Minor, int Patch)? ParseCore(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        // Strip build-metadata first (`+...`), then prerelease (`-...`).
        // Order matters: `1.0.0-alpha+sha` should become `1.0.0`, not
        // `1.0.0-alpha` (which would then fail to parse).
        var coreEnd = version.IndexOf('+', StringComparison.Ordinal);
        var core = coreEnd >= 0 ? version[..coreEnd] : version;
        var prereleaseStart = core.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseStart >= 0)
        {
            core = core[..prereleaseStart];
        }
        var match = Regex.Match(core.Trim(), @"^(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!match.Success)
        {
            return null;
        }
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return (major, minor, patch);
    }

    /// <summary>
    /// Returns true when <paramref name="current"/> is at least
    /// <paramref name="required"/> by SemVer-core comparison. Returns
    /// false (with a non-null <paramref name="reason"/>) when either
    /// version is unparsable, or when current &lt; required.
    /// </summary>
    public static bool Satisfies(string current, string required, out string? reason)
    {
        var c = ParseCore(current);
        var r = ParseCore(required);
        if (c is null)
        {
            reason = $"Cannot parse current polyphony version '{current}' as SemVer.";
            return false;
        }
        if (r is null)
        {
            reason = $"Cannot parse required polyphony version '{required}' as SemVer.";
            return false;
        }
        var ok = Compare(c.Value, r.Value) >= 0;
        reason = ok ? null
            : $"Polyphony {current} is older than required {required}. " +
              "Update the polyphony CLI before running this workflow.";
        return ok;
    }

    private static int Compare(
        (int Major, int Minor, int Patch) a,
        (int Major, int Minor, int Patch) b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        return a.Patch.CompareTo(b.Patch);
    }

    /// <summary>
    /// Read <c>workflow.metadata.min_polyphony_version</c> from a
    /// conductor workflow YAML file. Returns <c>null</c> if the file
    /// does not exist, the metadata block is missing, or the field is
    /// absent. Throws on unreadable / unparsable YAML so the caller
    /// surfaces a clear error.
    /// </summary>
    public static string? ReadMinVersionFromWorkflow(string workflowYamlPath)
    {
        if (!File.Exists(workflowYamlPath))
        {
            return null;
        }
        var yaml = File.ReadAllText(workflowYamlPath);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
        // Deserialize as a free-form dict so we don't have to model the
        // full conductor schema. Conductor itself owns that contract.
        var root = deserializer.Deserialize<Dictionary<object, object>>(yaml);
        if (root is null) return null;
        if (!TryGetChild(root, "workflow", out var workflow)) return null;
        if (!TryGetChild(workflow, "metadata", out var metadata)) return null;
        if (!TryGetString(metadata, "min_polyphony_version", out var value)) return null;
        return value;
    }

    private static bool TryGetChild(
        IDictionary<object, object> map, string key, out IDictionary<object, object> child)
    {
        if (map.TryGetValue(key, out var raw) && raw is IDictionary<object, object> dict)
        {
            child = dict;
            return true;
        }
        child = new Dictionary<object, object>();
        return false;
    }

    private static bool TryGetString(
        IDictionary<object, object> map, string key, out string? value)
    {
        if (map.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            return true;
        }
        value = null;
        return false;
    }
}
