using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Manifest;

/// <summary>
/// File-backed read/write for the run manifest at
/// <c>.polyphony/run.yaml</c>. Uses YamlDotNet with
/// <see cref="UnderscoredNamingConvention"/> to map between PascalCase
/// .NET fields and snake_case YAML keys (matches
/// <c>PolicyLoader</c>).
///
/// <para><see cref="Save"/> writes atomically via temp-file-and-rename
/// in the same directory. The topology hash is recomputed on every save
/// from <see cref="RunManifest.MergeGroups"/> so callers cannot persist
/// stale hashes.</para>
/// </summary>
public static class RunManifestStore
{
    /// <summary>Default relative path: <c>.polyphony/run.yaml</c>.</summary>
    public const string DefaultRelativePath = ".polyphony/run.yaml";

    /// <summary>
    /// Loads and validates a manifest from <paramref name="path"/>.
    /// Throws on missing file, malformed YAML, or invariant violations.
    /// </summary>
    public static RunManifest LoadOrThrow(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Run manifest not found at {path}.", path);
        }

        var yaml = File.ReadAllText(path);
        var manifest = Parse(yaml, path);
        RunManifestValidator.ValidateOrThrow(manifest, path);
        return manifest;
    }

    /// <summary>
    /// Parses YAML text into a manifest WITHOUT validating invariants.
    /// Use <see cref="RunManifestValidator.Validate"/> separately when
    /// you need partial parsing (e.g. surfacing all issues at once).
    /// </summary>
    public static RunManifest Parse(string yaml, string sourcePath = "<inline>")
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            return deserializer.Deserialize<RunManifest>(yaml)
                ?? throw new InvalidOperationException($"Empty run manifest: {sourcePath}");
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Failed to parse run manifest at {sourcePath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the manifest atomically to <paramref name="path"/>.
    /// Recomputes <see cref="RunManifest.TopologyHash"/> from the
    /// current <see cref="RunManifest.MergeGroups"/> before serializing
    /// (callers cannot persist a stale hash by accident).
    /// </summary>
    public static void Save(string path, RunManifest manifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(manifest);

        // Recompute topology hash so a stale value cannot leak through.
        manifest.TopologyHash = TopologyHasher.ComputeHash(manifest.MergeGroups);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var yaml = serializer.Serialize(manifest);

        // Ensure parent directory exists; YamlDotNet writes the text only.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteAtomic(path, yaml);
    }

    /// <summary>
    /// Atomic write via temp-file-and-rename (same directory; same
    /// volume guarantees rename atomicity). On Windows we use
    /// <see cref="File.Replace(string, string, string?)"/> when the
    /// destination already exists so the swap is atomic at the kernel
    /// level; on Unix the rename(2) call is atomic by spec.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        var baseName = Path.GetFileName(path);
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{baseName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, content);

            if (File.Exists(path))
            {
                // File.Replace is atomic on Windows, optimal on Unix too.
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // Best-effort cleanup; the throw rethrows the original cause.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
            throw;
        }
    }
}
