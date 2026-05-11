using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

/// <summary>
/// Loads <c>.polyphony-config/profile.yaml</c> into a <see cref="ProfileConfig"/>.
/// Follows the <c>LoadOrDefault</c> pattern: missing file or missing blocks
/// resolve to safe defaults rather than throwing, so existing repos that lack
/// a <c>research:</c> block continue to work without changes.
/// </summary>
public static class ProfileConfigLoader
{
    /// <summary>
    /// Loads the profile config from <paramref name="path"/>. Returns a
    /// default (research-disabled) <see cref="ProfileConfig"/> when the file
    /// is missing. Throws only on malformed YAML that <c>YamlDotNet</c>
    /// cannot parse.
    /// </summary>
    public static ProfileConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path))
            return new ProfileConfig();

        var yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
            return new ProfileConfig();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<ProfileConfig>(yaml)
            ?? new ProfileConfig();
    }

    /// <summary>
    /// Convenience overload that builds the profile.yaml path from a
    /// repo root directory. Equivalent to
    /// <c>LoadOrDefault(Path.Combine(repoRoot, ".polyphony-config", "profile.yaml"))</c>.
    /// </summary>
    public static ProfileConfig LoadOrDefaultFromRepo(string repoRoot) =>
        LoadOrDefault(Path.Combine(repoRoot, ".polyphony-config", "profile.yaml"));
}
