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
