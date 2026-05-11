namespace Polyphony.Configuration;

/// <summary>
/// Validates a <see cref="ProfileConfig"/> (specifically the
/// <see cref="ResearchConfig"/> block) against the V-R rule family.
/// Called by <c>validate-config</c> and by research-consuming code paths.
/// </summary>
public static class ProfileConfigValidator
{
    private static readonly HashSet<string> ValidPlatforms =
        new(StringComparer.OrdinalIgnoreCase) { "github", "ado" };

    /// <summary>
    /// Validates the <paramref name="profile"/>'s research block, using
    /// <paramref name="processConfig"/> only for platform-fallback
    /// resolution. Returns a <see cref="ConfigValidationResult"/> with
    /// errors from the V-R rule family.
    /// </summary>
    public static ConfigValidationResult Validate(
        ProfileConfig profile,
        ProcessConfig processConfig)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(processConfig);

        var errors = new List<ConfigValidationDiagnostic>();
        var warnings = new List<ConfigValidationDiagnostic>();

        var research = profile.Research;
        if (research is null || !research.Enabled)
        {
            return new ConfigValidationResult
            {
                IsValid = true,
                Errors = [],
                Warnings = [],
            };
        }

        // V-R1: repository is required when research is enabled
        if (string.IsNullOrWhiteSpace(research.Repository))
        {
            errors.Add(Error("V-R1",
                "research.repository is required when research.enabled is true."));
        }

        // Resolve the effective platform for subsequent rules
        var effectivePlatform = string.IsNullOrWhiteSpace(research.Platform)
            ? processConfig.Platform
            : research.Platform;

        // V-R2: platform must be a known value
        if (!ValidPlatforms.Contains(effectivePlatform))
        {
            errors.Add(Error("V-R2",
                $"research.platform '{effectivePlatform}' is not valid. " +
                "Valid values: github, ado."));
        }

        // V-R3: repository format must match the resolved platform
        if (!string.IsNullOrWhiteSpace(research.Repository)
            && ValidPlatforms.Contains(effectivePlatform))
        {
            var segments = research.Repository.Split('/');
            var isGitHub = string.Equals(effectivePlatform, "github", StringComparison.OrdinalIgnoreCase);

            if (isGitHub && segments.Length != 2)
            {
                errors.Add(Error("V-R3",
                    $"research.repository '{research.Repository}' must be 'owner/repo' " +
                    "format for the github platform."));
            }
            else if (!isGitHub && segments.Length != 3)
            {
                errors.Add(Error("V-R3",
                    $"research.repository '{research.Repository}' must be 'org/project/repo' " +
                    "format for the ado platform."));
            }
        }

        // V-R4: default_branch should not be empty when explicitly set
        if (research.DefaultBranch is not null
            && research.DefaultBranch.Length > 0
            && string.IsNullOrWhiteSpace(research.DefaultBranch))
        {
            warnings.Add(Warning("V-R4",
                "research.default_branch is whitespace-only; defaulting to 'main'."));
        }

        return new ConfigValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray(),
        };
    }

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };

    private static ConfigValidationDiagnostic Warning(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Warning };
}
