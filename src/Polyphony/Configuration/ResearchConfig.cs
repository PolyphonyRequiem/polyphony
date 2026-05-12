using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// YAML-friendly DTO for the <c>research:</c> block of
/// <c>.polyphony-config/profile.yaml</c>. Declares where the research
/// archive repo lives and which platform hosts it.
/// </summary>
/// <remarks>
/// Mutable string properties for YamlDotNet population. Use
/// <see cref="Research.ResearchTarget"/> for the validated, immutable
/// domain model consumed by <see cref="Research.IResearchStorage"/>.
/// </remarks>
public sealed class ResearchConfig
{
    /// <summary>
    /// The research archive repository in <c>owner/repo</c> format.
    /// Required when the <c>research:</c> block is present.
    /// </summary>
    [YamlMember(Alias = "repository")]
    public string? Repository { get; set; }

    /// <summary>
    /// Branch to target in the research repo. Defaults to <c>main</c>.
    /// </summary>
    [YamlMember(Alias = "branch")]
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Platform hosting the research repo: <c>github</c> or <c>ado</c>.
    /// Required when the <c>research:</c> block is present.
    /// </summary>
    [YamlMember(Alias = "platform")]
    public string? Platform { get; set; }
}
