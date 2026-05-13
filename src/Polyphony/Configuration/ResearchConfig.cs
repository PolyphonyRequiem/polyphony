using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// YAML-friendly DTO for the <c>research:</c> block in
/// <c>.polyphony-config/profile.yaml</c>. Describes how polyphony reaches
/// a sibling repository used for persistent research storage.
/// </summary>
/// <remarks>
/// Auth defaults to the platform-router's existing credential resolution
/// (GH_TOKEN via <see cref="Infrastructure.Processes.GhTokenResolver"/>).
/// The optional <see cref="Auth"/> block lets operators override with an
/// explicit env-var token for cross-platform or service-account scenarios
/// without introducing new secret plumbing in the common path.
/// </remarks>
public sealed class ResearchConfig
{
    /// <summary>
    /// Owner/repo slug of the sibling research repository,
    /// e.g. <c>PolyphonyRequiem/polyphony-research</c>.
    /// </summary>
    [YamlMember(Alias = "repository")]
    public string? Repository { get; set; }

    /// <summary>
    /// Path prefix within the research repo under which files are stored.
    /// Defaults to the repo root when empty.
    /// </summary>
    [YamlMember(Alias = "base_path")]
    public string BasePath { get; set; } = "";

    /// <summary>
    /// Target branch in the research repo. Defaults to <c>main</c>.
    /// </summary>
    [YamlMember(Alias = "branch")]
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Optional auth override. When <c>null</c>, polyphony reuses the
    /// platform-router credentials (GH_TOKEN). When set, the specified
    /// env-var is read at call time and passed per-process to <c>gh api</c>.
    /// </summary>
    [YamlMember(Alias = "auth")]
    public ResearchAuthConfig? Auth { get; set; }

    /// <summary>
    /// Maximum number of <c>deep_researcher</c> escalations per
    /// <c>research_needs</c> call. Default <c>1</c> per the locked design
    /// in plan-3131.md (conservative — every escalation runs the expensive
    /// tier once). <c>0</c> disables escalation entirely (research stays
    /// archive-only). Today the research workflow only honors values
    /// <c>0</c> or <c>1</c>; values &gt; <c>1</c> are accepted by config
    /// for forward compatibility but are clamped to <c>1</c> by the
    /// workflow until a proper escalation loop is wired
    /// (tracked in the AB#3134 follow-up notes).
    /// </summary>
    [YamlMember(Alias = "escalation_cap")]
    public int EscalationCap { get; set; } = 1;
}

/// <summary>
/// Optional auth override for the research storage. When present, the
/// token is read from the named environment variable and scoped to the
/// child <c>gh api</c> process — it is never written to the parent
/// process's environment.
/// </summary>
public sealed class ResearchAuthConfig
{
    /// <summary>
    /// Name of the environment variable that holds the auth token.
    /// Example: <c>RESEARCH_GH_TOKEN</c>.
    /// </summary>
    [YamlMember(Alias = "token_env_var")]
    public string? TokenEnvVar { get; set; }
}
