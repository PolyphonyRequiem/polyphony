using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

public static class ProcessConfigLoader
{
    public static ProcessConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Process config not found: {path}");

        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<ProcessConfig>(yaml)
            ?? throw new InvalidOperationException("Failed to parse process config");

        if (config.SchemaVersion > 1)
            throw new InvalidOperationException(
                $"Unsupported process config schema version {config.SchemaVersion}. " +
                "This version of Polyphony supports schema_version 0 (absent) and 1.");

        return config;
    }

    /// <summary>
    /// Returns the parent type name for the given type, or null if none is set.
    /// </summary>
    public static string? GetParentTypeName(ProcessConfig config, string typeName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (typeName is null) throw new ArgumentNullException(nameof(typeName));
        if (!config.Types.TryGetValue(typeName, out var typeConfig))
            throw new ArgumentException($"Type '{typeName}' not found in process config.", nameof(typeName));
        return typeConfig.Parent;
    }
}
