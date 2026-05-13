using System.Text.RegularExpressions;

namespace Polyphony.Configuration;

/// <summary>
/// Config-load-time checks for the <c>research:</c> block of
/// <c>.polyphony-config/profile.yaml</c>. Plugged into
/// <see cref="ConfigValidator"/> (V-22). Run independently in tests via
/// <see cref="Validate"/>.
/// </summary>
public static partial class ResearchConfigValidator
{
    /// <summary>Rule: research.repository is required and must be owner/repo format.</summary>
    public const string RepositoryRequiredRuleId = "V-22";

    /// <summary>Rule: auth.token_env_var, when specified, must be non-empty.</summary>
    public const string AuthTokenEnvVarEmptyRuleId = "V-23";

    /// <summary>Rule: base_path must not contain path-traversal segments.</summary>
    public const string BasePathTraversalRuleId = "V-24";

    /// <summary>Rule: escalation_cap must be a non-negative integer.</summary>
    public const string EscalationCapNegativeRuleId = "V-25";

    // owner/repo — at least one char each, no whitespace.
    [GeneratedRegex(@"^[^\s/]+/[^\s/]+$")]
    private static partial Regex OwnerRepoPattern();

    /// <summary>
    /// Validate the <c>research:</c> block on the given
    /// <paramref name="profileConfig"/>. Returns an empty list when the
    /// block is absent or valid.
    /// </summary>
    public static IReadOnlyList<ConfigValidationDiagnostic> Validate(ProfileConfig profileConfig)
    {
        ArgumentNullException.ThrowIfNull(profileConfig);

        var errors = new List<ConfigValidationDiagnostic>();

        if (profileConfig.Research is null)
        {
            return errors;
        }

        var research = profileConfig.Research;

        // V-22: repository is required and must look like owner/repo.
        if (string.IsNullOrWhiteSpace(research.Repository))
        {
            errors.Add(Error(RepositoryRequiredRuleId,
                "research.repository is required when the research block is present. " +
                "Specify the sibling repository as 'owner/repo' (e.g. 'PolyphonyRequiem/polyphony-research')."));
        }
        else if (!OwnerRepoPattern().IsMatch(research.Repository))
        {
            errors.Add(Error(RepositoryRequiredRuleId,
                $"research.repository '{research.Repository}' is not a valid owner/repo slug. " +
                "Expected format: 'owner/repo' (e.g. 'PolyphonyRequiem/polyphony-research')."));
        }

        // V-23: if auth override is specified, token_env_var must be non-empty.
        if (research.Auth is not null
            && string.IsNullOrWhiteSpace(research.Auth.TokenEnvVar))
        {
            errors.Add(Error(AuthTokenEnvVarEmptyRuleId,
                "research.auth.token_env_var must be non-empty when the auth block is present. " +
                "Either specify an environment variable name or remove the auth block to use " +
                "platform-router credentials."));
        }

        // V-24: base_path must not contain path-traversal segments.
        if (!string.IsNullOrEmpty(research.BasePath)
            && ContainsTraversal(research.BasePath))
        {
            errors.Add(Error(BasePathTraversalRuleId,
                $"research.base_path '{research.BasePath}' contains path-traversal segments " +
                "('..' or leading '/'). Use a simple relative path."));
        }

        // V-25: escalation_cap must be a non-negative integer. The workflow
        // currently only honors 0 (escalation disabled) or 1 (single-shot
        // escalation, the design default). Higher values are accepted here
        // for forward compatibility but are clamped to 1 at the workflow
        // boundary until a proper escalation loop is wired.
        if (research.EscalationCap < 0)
        {
            errors.Add(Error(EscalationCapNegativeRuleId,
                $"research.escalation_cap '{research.EscalationCap}' must be a non-negative integer. " +
                "Use 0 to disable deep-researcher escalation entirely, or 1 (default) for a single-shot " +
                "escalation per research_needs call."));
        }

        return errors;
    }

    private static bool ContainsTraversal(string path)
    {
        // Reject absolute paths (leading /) and dot-segments
        if (path.StartsWith('/') || path.StartsWith('\\'))
            return true;

        var segments = path.Split('/', '\\');
        return segments.Any(s => s == "..");
    }

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };
}
