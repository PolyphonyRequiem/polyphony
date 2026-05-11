using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// Configuration for the <c>research:</c> block in
/// <c>.polyphony-config/profile.yaml</c>. Describes the sibling research
/// repository, authentication, and path conventions.
/// </summary>
/// <remarks>
/// This type is parsed but not yet consumed at runtime — no workflow or
/// command reads <c>profile.yaml</c> today. The loader + validator ship
/// ahead of consumption so the schema stabilises first.
/// </remarks>
public sealed class ResearchConfig
{
    /// <summary>
    /// Required. The sibling research repository in <c>owner/name</c> form
    /// (e.g. <c>PolyphonyRequiem/polyphony-research</c>).
    /// </summary>
    [YamlMember(Alias = "repo")]
    public string? Repo { get; set; }

    /// <summary>
    /// Branch to read from in the research repository. Default: <c>main</c>.
    /// </summary>
    [YamlMember(Alias = "branch")]
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Hosting platform for the research repository. One of the
    /// <see cref="ResearchPlatform"/> constants. Default: <c>github</c>.
    /// </summary>
    [YamlMember(Alias = "platform")]
    public string Platform { get; set; } = "github";

    /// <summary>
    /// Authentication configuration for the research repository.
    /// Required — at least <see cref="ResearchAuthConfig.EnvVar"/> must be set.
    /// </summary>
    [YamlMember(Alias = "auth")]
    public ResearchAuthConfig? Auth { get; set; }

    /// <summary>
    /// Path conventions within the research and source repositories.
    /// Optional — defaults are applied when absent.
    /// </summary>
    [YamlMember(Alias = "paths")]
    public ResearchPathsConfig? Paths { get; set; }
}

/// <summary>
/// Authentication block nested under <c>research.auth</c>.
/// </summary>
public sealed class ResearchAuthConfig
{
    /// <summary>
    /// Name of the environment variable holding a PAT for the research
    /// repository (e.g. <c>RESEARCH_PAT</c>).
    /// </summary>
    [YamlMember(Alias = "env_var")]
    public string? EnvVar { get; set; }
}

/// <summary>
/// Path conventions nested under <c>research.paths</c>.
/// </summary>
public sealed class ResearchPathsConfig
{
    /// <summary>
    /// Root directory in the research repository for archived artefacts.
    /// Default: <c>research/</c>. Must be POSIX-style and non-absolute.
    /// </summary>
    [YamlMember(Alias = "archive_root")]
    public string ArchiveRoot { get; set; } = "research/";

    /// <summary>
    /// Scratch directory in the source repository for ephemeral research
    /// artefacts. Default: <c>research/scratch/</c>. Must be POSIX-style
    /// and non-absolute.
    /// </summary>
    [YamlMember(Alias = "scratch_root")]
    public string ScratchRoot { get; set; } = "research/scratch/";
}

/// <summary>
/// Canonical string constants for <see cref="ResearchConfig.Platform"/>.
/// Mirrors the <see cref="Policy.RootFallbackAutoDecide"/> pattern so the
/// YAML deserializer accepts the literal token verbatim.
/// </summary>
public static class ResearchPlatform
{
    /// <summary>GitHub-hosted research repository (default).</summary>
    public const string GitHub = "github";

    /// <summary>Azure DevOps-hosted research repository.</summary>
    public const string Ado = "ado";

    /// <summary>True when <paramref name="value"/> is one of the canonical tokens.</summary>
    public static bool IsValid(string value) =>
        value is GitHub or Ado;
}
