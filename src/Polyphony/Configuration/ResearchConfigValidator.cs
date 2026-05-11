namespace Polyphony.Configuration;

/// <summary>
/// Validates a <see cref="ResearchConfig"/> against rules R-1 through R-6.
/// All rules produce errors (block execution). Returns a
/// <see cref="ConfigValidationResult"/> so callers can inspect individual
/// diagnostics.
/// </summary>
public static class ResearchConfigValidator
{
    /// <summary>
    /// Validates the given <paramref name="config"/> and returns a structured result.
    /// </summary>
    public static ConfigValidationResult Validate(ResearchConfig config)
    {
        var errors = new List<ConfigValidationDiagnostic>();

        // R-1: repo is required
        if (string.IsNullOrWhiteSpace(config.Repo))
        {
            errors.Add(Error("R-1", "research.repo is required."));
        }
        else
        {
            // R-2: repo must be owner/name shaped
            if (!IsValidRepoShape(config.Repo))
            {
                errors.Add(Error("R-2",
                    $"research.repo '{config.Repo}' is not in 'owner/name' form. " +
                    "Expected exactly two non-empty segments separated by '/'."));
            }
        }

        // R-3: platform must be a known value
        if (!ResearchPlatform.IsValid(config.Platform))
        {
            errors.Add(Error("R-3",
                $"research.platform '{config.Platform}' is not a known platform. " +
                $"Expected '{ResearchPlatform.GitHub}' or '{ResearchPlatform.Ado}'."));
        }

        // R-4: auth block is required with env_var
        if (config.Auth is null)
        {
            errors.Add(Error("R-4", "research.auth is required."));
        }
        else
        {
            // R-5: auth.env_var must be non-empty when auth is present
            if (string.IsNullOrWhiteSpace(config.Auth.EnvVar))
            {
                errors.Add(Error("R-5",
                    "research.auth.env_var is required. " +
                    "Set the name of the environment variable holding the PAT " +
                    "(e.g. 'RESEARCH_PAT')."));
            }
        }

        // R-6: paths must be non-absolute and POSIX-style (no backslashes)
        if (config.Paths is not null)
        {
            ValidatePath("research.paths.archive_root", config.Paths.ArchiveRoot, errors);
            ValidatePath("research.paths.scratch_root", config.Paths.ScratchRoot, errors);
        }

        return new ConfigValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors.ToArray(),
            Warnings = [],
        };
    }

    private static bool IsValidRepoShape(string repo)
    {
        var parts = repo.Split('/');
        if (parts.Length != 2)
            return false;

        var owner = parts[0].Trim();
        var name = parts[1].Trim();

        return owner.Length > 0 && name.Length > 0
            && !owner.Any(char.IsWhiteSpace)
            && !name.Any(char.IsWhiteSpace);
    }

    private static void ValidatePath(string fieldName, string value, List<ConfigValidationDiagnostic> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (value.Contains('\\'))
        {
            errors.Add(Error("R-6",
                $"{fieldName} '{value}' contains backslashes. " +
                "Paths must be POSIX-style (use '/' as separator)."));
        }

        if (IsAbsolutePath(value))
        {
            errors.Add(Error("R-6",
                $"{fieldName} '{value}' is an absolute path. " +
                "Paths must be relative."));
        }
    }

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };
}
