using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

/// <summary>
/// Loads the <c>research:</c> block from <c>.polyphony-config/profile.yaml</c>
/// into a <see cref="ResearchConfig"/>. Returns <c>null</c> when the file is
/// missing or contains no <c>research:</c> key. Applies documented defaults
/// and validates required fields when a block is present.
/// </summary>
/// <remarks>
/// This loader is parsed-but-not-consumed: no workflow or command reads
/// <c>profile.yaml</c> at runtime today. Shipping the loader ahead of
/// consumption lets the schema stabilise under tests before wiring.
/// </remarks>
public static class ResearchConfigLoader
{
    /// <summary>
    /// Loads and validates the <c>research:</c> block from <paramref name="path"/>.
    /// Returns <c>null</c> when the file does not exist or contains no
    /// <c>research:</c> key.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>research:</c> block exists but fails validation (missing
    /// required field or invalid value).
    /// </exception>
    public static ResearchConfig? LoadOrDefault(string path)
    {
        if (!File.Exists(path))
            return null;

        var yaml = File.ReadAllText(path);
        var config = Parse(yaml);
        if (config is null)
            return null;

        ApplyDefaults(config);
        ValidateOrThrow(config);
        return config;
    }

    /// <summary>
    /// Parses the <c>research:</c> block from raw YAML without applying
    /// defaults or validation. Returns <c>null</c> when the block is absent.
    /// </summary>
    public static ResearchConfig? Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var envelope = deserializer.Deserialize<ProfileResearchEnvelope>(yaml);
        return envelope?.Research;
    }

    /// <summary>
    /// Applies documented defaults to optional fields on <paramref name="config"/>.
    /// Idempotent — safe to call repeatedly.
    /// </summary>
    public static void ApplyDefaults(ResearchConfig config)
    {
        config.Paths ??= new ResearchPathsConfig();
    }

    /// <summary>
    /// Serializes the <c>research:</c> block back to YAML for round-trip
    /// verification. The output contains only the <c>research:</c> key.
    /// </summary>
    public static string Serialize(ResearchConfig config)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();

        var envelope = new ProfileResearchEnvelope { Research = config };
        return serializer.Serialize(envelope);
    }

    private static void ValidateOrThrow(ResearchConfig config)
    {
        var result = ResearchConfigValidator.Validate(config);
        if (!result.IsValid)
        {
            var messages = string.Join("; ", result.Errors.Select(e => $"[{e.RuleId}] {e.Message}"));
            throw new InvalidOperationException(
                $"Invalid research config in profile.yaml: {messages}");
        }
    }

    /// <summary>
    /// Internal envelope for extracting only the <c>research:</c> block
    /// from <c>profile.yaml</c> via <c>IgnoreUnmatchedProperties()</c>.
    /// </summary>
    private sealed class ProfileResearchEnvelope
    {
        [YamlMember(Alias = "research")]
        public ResearchConfig? Research { get; set; }
    }
}
