namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Resolves an Azure DevOps Personal Access Token from the environment.
///
/// <para>
/// Resolution order (mirrors the convention used by the <c>az devops</c> CLI
/// and the standard ADO pipelines task vocabulary):
/// </para>
/// <list type="number">
///   <item><c>AZURE_DEVOPS_EXT_PAT</c> — the variable the <c>az devops</c>
///     CLI reads when set, and the canonical opt-in for PAT-based auth.</item>
///   <item><c>AZURE_DEVOPS_PAT</c> — long-standing alternative used by older
///     scripts and by some pipeline templates; honoured for compatibility.</item>
///   <item><c>SYSTEM_ACCESSTOKEN</c> — the OAuth token Azure Pipelines
///     injects into job environments. Treated as a PAT by the connection-data
///     endpoint when the agent is configured to expose it.</item>
/// </list>
///
/// <para>
/// Subsequent PRs may extend this with a config-file fallback or with an
/// <c>az account get-access-token</c> shell-out for AAD auth (see twig2's
/// <c>AzCliAuthProvider</c> for the established pattern). For Phase 5 the
/// scope is environment-only — that is sufficient for the auth probe and for
/// CI invocations.
/// </para>
///
/// <para>
/// This resolver is stateless and thread-safe; safe to register as a singleton.
/// </para>
/// </summary>
public sealed class AdoTokenResolver
{
    /// <summary>
    /// Canonical env var read by <c>az devops</c>; first in precedence.
    /// </summary>
    public const string AzureDevOpsExtPatVar = "AZURE_DEVOPS_EXT_PAT";

    /// <summary>
    /// Compatibility alias used by older scripts.
    /// </summary>
    public const string AzureDevOpsPatVar = "AZURE_DEVOPS_PAT";

    /// <summary>
    /// OAuth token injected by Azure Pipelines into job environments.
    /// </summary>
    public const string SystemAccessTokenVar = "SYSTEM_ACCESSTOKEN";

    private static readonly string[] DefaultPrecedence =
    [
        AzureDevOpsExtPatVar,
        AzureDevOpsPatVar,
        SystemAccessTokenVar,
    ];

    private readonly Func<string, string?> _envReader;
    private readonly IReadOnlyList<string> _precedence;

    /// <summary>
    /// Production constructor. Reads from <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// using the default precedence list.
    /// </summary>
    public AdoTokenResolver()
        : this(Environment.GetEnvironmentVariable, DefaultPrecedence)
    {
    }

    /// <summary>
    /// Test-only constructor. Allows tests to inject a deterministic
    /// environment-variable reader and override the precedence list.
    /// </summary>
    /// <param name="envReader">Function that returns the value of the named env var, or <c>null</c> when unset.</param>
    /// <param name="precedence">Ordered list of variable names to consult. Must be non-empty.</param>
    internal AdoTokenResolver(Func<string, string?> envReader, IReadOnlyList<string> precedence)
    {
        _envReader = envReader ?? throw new ArgumentNullException(nameof(envReader));
        if (precedence is null || precedence.Count == 0)
            throw new ArgumentException("Precedence list must contain at least one variable name.", nameof(precedence));
        _precedence = precedence;
    }

    /// <summary>
    /// Returns the first non-empty PAT found by walking the precedence list,
    /// or <c>null</c> when none of the variables are set. Whitespace-only
    /// values are treated as unset so a stray empty assignment cannot mask a
    /// real downstream variable.
    /// </summary>
    public string? Resolve()
    {
        foreach (var name in _precedence)
        {
            var value = _envReader(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }
}
