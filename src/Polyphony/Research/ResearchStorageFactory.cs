using Polyphony.Configuration;

namespace Polyphony.Research;

/// <summary>
/// Creates the correct <see cref="IResearchStorage"/> implementation for the
/// platform declared in a <see cref="ResearchConfig"/>. This is the
/// single point where platform → implementation routing happens for
/// research storage.
/// </summary>
/// <remarks>
/// <para>
/// The factory validates that the config has the required fields and
/// normalizes the platform to lowercase before selecting the implementation.
/// Callers should run <see cref="ResearchConfigValidator"/> before calling
/// <see cref="Create"/> — the factory rejects invalid configs with
/// <see cref="ArgumentException"/> but does not produce structured
/// diagnostics.
/// </para>
/// <para>
/// This follows the platform-router pattern used elsewhere in polyphony:
/// the <c>platform</c> field on the config determines which concrete
/// implementation is instantiated. Unlike the PR infrastructure (where
/// <see cref="Infrastructure.Processes.IGhClient"/> and
/// <see cref="Infrastructure.AzureDevOps.IAdoClient"/> are separate
/// interfaces), research storage uses a single
/// <see cref="IResearchStorage"/> interface so consumers don't need
/// platform-conditional code.
/// </para>
/// </remarks>
public static class ResearchStorageFactory
{
    /// <summary>
    /// Create an <see cref="IResearchStorage"/> from a validated
    /// <see cref="ResearchConfig"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="config"/> has a missing or unrecognized
    /// platform, or a missing repository.
    /// </exception>
    public static IResearchStorage Create(ResearchConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Repository))
            throw new ArgumentException(
                "ResearchConfig.Repository is required.", nameof(config));

        if (string.IsNullOrWhiteSpace(config.Platform))
            throw new ArgumentException(
                "ResearchConfig.Platform is required.", nameof(config));

        var target = new ResearchTarget(
            Repository: config.Repository.Trim(),
            Branch: string.IsNullOrWhiteSpace(config.Branch) ? "main" : config.Branch.Trim(),
            Platform: config.Platform.Trim().ToLowerInvariant());

        return target.Platform switch
        {
            "github" => new GitHubResearchStorage(target),
            "ado" => new AdoResearchStorage(target),
            _ => throw new ArgumentException(
                $"Unsupported research platform: '{config.Platform}'. " +
                "Valid values: github, ado.",
                nameof(config)),
        };
    }
}
