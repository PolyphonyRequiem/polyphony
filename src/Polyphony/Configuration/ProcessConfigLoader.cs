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
}
