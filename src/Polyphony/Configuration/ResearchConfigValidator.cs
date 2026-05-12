namespace Polyphony.Configuration;

/// <summary>
/// Config-load-time checks for the <c>research:</c> block of
/// <c>.polyphony-config/profile.yaml</c>. Plugged into the
/// <c>validate-config</c> command alongside the process-config rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Validation fires only when the <c>research:</c> block is
/// present. A missing block means "no research features" and produces no
/// diagnostics — it is not an error to omit research.
/// </para>
/// <para>
/// When the block IS present, the validator enforces that the required
/// fields (<c>repository</c>, <c>platform</c>) are supplied and that
/// <c>platform</c> is a recognized value. An empty or whitespace-only
/// <c>branch</c> (which requires explicit override of the <c>"main"</c>
/// default) is also rejected.
/// </para>
/// </remarks>
public static class ResearchConfigValidator
{
    /// <summary>V-22: <c>repository</c> is required when <c>research:</c> block is present.</summary>
    public const string MissingRepositoryRuleId = "V-22";

    /// <summary>V-23: <c>platform</c> is required when <c>research:</c> block is present.</summary>
    public const string MissingPlatformRuleId = "V-23";

    /// <summary>V-24: <c>platform</c> must be <c>github</c> or <c>ado</c>.</summary>
    public const string InvalidPlatformRuleId = "V-24";

    /// <summary>V-25: <c>branch</c> must not be empty or whitespace.</summary>
    public const string EmptyBranchRuleId = "V-25";

    private static readonly HashSet<string> ValidPlatforms =
        new(StringComparer.OrdinalIgnoreCase) { "github", "ado" };

    /// <summary>
    /// Validate the <c>research:</c> block from a <see cref="ProfileConfig"/>.
    /// Returns an empty list when the block is absent or well-formed.
    /// </summary>
    public static IReadOnlyList<ConfigValidationDiagnostic> Validate(ProfileConfig profileConfig)
    {
        ArgumentNullException.ThrowIfNull(profileConfig);
        return Validate(profileConfig.Research);
    }

    /// <summary>
    /// Validate a <see cref="ResearchConfig"/> directly. Returns an empty
    /// list when <paramref name="config"/> is null (block absent) or
    /// well-formed.
    /// </summary>
    public static IReadOnlyList<ConfigValidationDiagnostic> Validate(ResearchConfig? config)
    {
        var errors = new List<ConfigValidationDiagnostic>();

        if (config is null)
            return errors;

        if (string.IsNullOrWhiteSpace(config.Repository))
        {
            errors.Add(Error(MissingRepositoryRuleId,
                "research.repository is required when the research: block is present. " +
                "Specify the archive repo in owner/repo format (e.g. 'myorg/my-research')."));
        }

        if (string.IsNullOrWhiteSpace(config.Platform))
        {
            errors.Add(Error(MissingPlatformRuleId,
                "research.platform is required when the research: block is present. " +
                "Valid values: github, ado."));
        }
        else if (!ValidPlatforms.Contains(config.Platform))
        {
            errors.Add(Error(InvalidPlatformRuleId,
                $"research.platform '{config.Platform}' is not recognized. " +
                "Valid values: github, ado."));
        }

        if (string.IsNullOrWhiteSpace(config.Branch))
        {
            errors.Add(Error(EmptyBranchRuleId,
                "research.branch must not be empty or whitespace. " +
                "Omit the field to use the default ('main'), or specify a valid branch name."));
        }

        return errors;
    }

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };
}
