namespace Polyphony.Research;

/// <summary>
/// GitHub-backed research storage. Targets a research archive hosted on
/// GitHub, identified by the <see cref="IResearchStorage.Target"/>.
/// </summary>
/// <remarks>
/// File-level operations (read, write, list) will be added when the
/// research-augmented agent PRs (Issues 2–4 on Epic #3107) land. Those
/// operations will shell out to <c>gh</c> or use the GitHub REST API,
/// following the same pattern as
/// <see cref="Infrastructure.Processes.IGhClient"/>.
/// </remarks>
public sealed class GitHubResearchStorage(ResearchTarget target) : IResearchStorage
{
    /// <inheritdoc />
    public ResearchTarget Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
}
