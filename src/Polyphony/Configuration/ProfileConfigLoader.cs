using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

/// <summary>
/// Loads and parses <c>.polyphony-config/profile.yaml</c> into a
/// <see cref="ProfileConfig"/>. Mirrors the load pattern of
/// <see cref="ProcessConfigLoader"/> but is intentionally lenient:
/// unrecognized YAML keys are silently ignored so the file can carry
/// conductor-only sections (project, tech_stack, conventions, etc.)
/// without breaking polyphony.
/// </summary>
public static class ProfileConfigLoader
{
    /// <summary>
    /// Loads and deserializes <paramref name="path"/> into a
    /// <see cref="ProfileConfig"/>. Returns a default (empty) instance
    /// when the file does not exist — profile.yaml is optional in many
    /// repos today and its absence should not block execution.
    /// </summary>
    public static ProfileConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new ProfileConfig();
        }

        var yaml = File.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new ProfileConfig();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<ProfileConfig>(yaml)
            ?? new ProfileConfig();
    }
}
