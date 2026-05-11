using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// Narrow DTO for <c>.polyphony-config/profile.yaml</c>. Intentionally
/// models only the blocks the runtime needs (currently just
/// <see cref="Research"/>). Remaining keys (<c>project:</c>,
/// <c>tech_stack:</c>, <c>build:</c>, etc.) are consumed by agent
/// guidance templates and ignored here via
/// <c>IgnoreUnmatchedProperties()</c> at load time.
/// </summary>
public sealed class ProfileConfig
{
    /// <summary>
    /// Optional <c>research:</c> block controlling the research storage
    /// abstraction. When absent or <see cref="ResearchConfig.Enabled"/>
    /// is <c>false</c>, research-dependent workflow steps are skipped.
    /// </summary>
    [YamlMember(Alias = "research")]
    public ResearchConfig? Research { get; set; }
}
