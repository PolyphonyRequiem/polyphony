using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// YAML-friendly DTO for the <c>research:</c> block in
/// <c>.polyphony-config/profile.yaml</c>. Controls whether the research
/// storage abstraction is wired up and which sibling repo it targets.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Platform"/> is intentionally nullable in the DTO layer.
/// When omitted, callers resolve it from the process config's
/// <see cref="ProcessConfig.Platform"/> — that resolution lives in
/// <see cref="ResearchConfigResolver"/>, not here.
/// </para>
/// <para>
/// <see cref="Repository"/> format is platform-dependent:
/// <list type="bullet">
///   <item>GitHub: <c>owner/repo</c> (two segments)</item>
///   <item>ADO: <c>org/project/repo</c> (three segments)</item>
/// </list>
/// Validation enforces the correct shape based on the resolved platform.
/// </para>
/// </remarks>
public sealed class ResearchConfig
{
    /// <summary>
    /// Master switch. When <c>false</c> (default), no research store is
    /// resolved and research-dependent workflow steps are skipped.
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Slug identifying the sibling research repository.
    /// GitHub: <c>owner/repo</c>. ADO: <c>org/project/repo</c>.
    /// Required when <see cref="Enabled"/> is <c>true</c>.
    /// </summary>
    [YamlMember(Alias = "repository")]
    public string? Repository { get; set; }

    /// <summary>
    /// Platform hosting the research repo. <c>github</c> or <c>ado</c>.
    /// When omitted, falls back to <see cref="ProcessConfig.Platform"/>.
    /// </summary>
    [YamlMember(Alias = "platform")]
    public string? Platform { get; set; }

    /// <summary>
    /// Branch to read from / write to. Defaults to <c>main</c>.
    /// </summary>
    [YamlMember(Alias = "default_branch")]
    public string DefaultBranch { get; set; } = "main";
}

/// <summary>
/// Resolves a raw <see cref="ResearchConfig"/> DTO (nullable platform,
/// missing defaults) into a fully populated <see cref="EffectiveResearchConfig"/>
/// by falling back to <see cref="ProcessConfig.Platform"/> when the
/// research-specific platform is omitted.
/// </summary>
public static class ResearchConfigResolver
{
    /// <summary>
    /// Produces an effective config by filling in defaults. Returns
    /// <c>null</c> when research is disabled or absent.
    /// </summary>
    public static EffectiveResearchConfig? Resolve(ResearchConfig? raw, ProcessConfig processConfig)
    {
        ArgumentNullException.ThrowIfNull(processConfig);

        if (raw is null || !raw.Enabled)
            return null;

        var platform = string.IsNullOrWhiteSpace(raw.Platform)
            ? processConfig.Platform
            : raw.Platform;

        return new EffectiveResearchConfig(
            Repository: raw.Repository ?? "",
            Platform: platform,
            DefaultBranch: string.IsNullOrWhiteSpace(raw.DefaultBranch) ? "main" : raw.DefaultBranch);
    }
}

/// <summary>
/// Fully resolved research configuration — no nulls, no fallback logic.
/// Produced by <see cref="ResearchConfigResolver.Resolve"/>.
/// </summary>
public sealed record EffectiveResearchConfig(
    string Repository,
    string Platform,
    string DefaultBranch);
