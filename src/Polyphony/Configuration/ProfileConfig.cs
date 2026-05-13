using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// Top-level model for <c>.polyphony-config/profile.yaml</c>. Only the
/// blocks polyphony actually needs at runtime are bound here; everything
/// else (project metadata, tech_stack, conventions, etc.) is consumed by
/// conductor workflows and silently ignored via
/// <c>IgnoreUnmatchedProperties</c> at load time.
/// </summary>
public sealed class ProfileConfig
{
    /// <summary>
    /// Configuration for the sibling research repository. <c>null</c> when
    /// the profile has no <c>research:</c> block (the common case today).
    /// </summary>
    [YamlMember(Alias = "research")]
    public ResearchConfig? Research { get; set; }
}
