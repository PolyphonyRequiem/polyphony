using Polyphony.Configuration;

namespace Polyphony.Research;

/// <summary>
/// Selects the correct <see cref="IResearchStore"/> implementation based
/// on the resolved research platform. This is the "platform router" seam
/// for the research storage abstraction.
/// </summary>
/// <remarks>
/// The factory pattern (rather than direct DI registration of
/// <see cref="IResearchStore"/>) keeps the decision point visible and
/// testable. Tests can verify that the correct implementation is produced
/// for each platform without needing live network calls.
/// </remarks>
public sealed class ResearchStoreFactory
{
    /// <summary>
    /// Creates the appropriate <see cref="IResearchStore"/> for the given
    /// effective config.  Returns <c>null</c> when
    /// <paramref name="config"/> is <c>null</c> (research disabled).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="config"/> specifies an unknown platform.
    /// This should be unreachable after validation (V-R2).
    /// </exception>
    public IResearchStore? Create(
        EffectiveResearchConfig? config,
        IGitHubResearchClient? gitHubClient = null,
        IAdoResearchClient? adoClient = null)
    {
        if (config is null)
            return null;

        return config.Platform.ToLowerInvariant() switch
        {
            "github" => new GitHubResearchStore(
                gitHubClient ?? throw new InvalidOperationException(
                    "GitHub research client is required when research.platform is 'github'."),
                config),
            "ado" => new AdoResearchStore(
                adoClient ?? throw new InvalidOperationException(
                    "ADO research client is required when research.platform is 'ado'."),
                config),
            _ => throw new InvalidOperationException(
                $"Unknown research platform '{config.Platform}'. " +
                "Valid values: github, ado.")
        };
    }
}
