using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

/// <summary>
/// Typed model for <c>.polyphony-config/profile.yaml</c>. Currently loads
/// only the <c>research:</c> block; other top-level keys (project,
/// tech_stack, build, conventions) are ignored via
/// <see cref="DeserializerBuilder.IgnoreUnmatchedProperties"/>.
/// </summary>
public sealed class ProfileConfig
{
    /// <summary>
    /// Optional research archive configuration. When present, declares
    /// the repo, branch, and platform for the research storage layer.
    /// When absent, research features are unavailable and no error is raised.
    /// </summary>
    [YamlMember(Alias = "research")]
    public ResearchConfig? Research { get; set; }
}

/// <summary>
/// Loads and parses <c>.polyphony-config/profile.yaml</c> into a
/// <see cref="ProfileConfig"/>. Mirrors the shape of
/// <see cref="ProcessConfigLoader"/> but targets profile.yaml.
/// </summary>
public static class ProfileConfigLoader
{
    /// <summary>
    /// Load a profile config from <paramref name="path"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <paramref name="path"/> does not exist on disk.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the YAML is unparseable.
    /// </exception>
    public static ProfileConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile config not found: {path}", path);

        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read profile config '{path}': {ex.Message}", ex);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            return deserializer.Deserialize<ProfileConfig>(yaml)
                ?? throw new InvalidOperationException(
                    $"Failed to parse profile config '{path}': file is empty or contains only comments.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse profile config '{path}' at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}",
                ex);
        }
    }
}
