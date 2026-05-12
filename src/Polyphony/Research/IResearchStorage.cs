namespace Polyphony.Research;

/// <summary>
/// Cross-platform abstraction for targeting a research archive repository.
/// Implementations exist per platform (<see cref="GitHubResearchStorage"/>,
/// <see cref="AdoResearchStorage"/>) and are created by
/// <see cref="ResearchStorageFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// This interface is the foundation that research-augmented agents (Issues 2–4
/// on Epic #3107) will build on. The current surface is deliberately minimal —
/// just the resolved <see cref="Target"/> — because the operations agents need
/// (read/write/list artifacts) are defined by the agent PRs. The contract is:
/// any code that needs to interact with the research repo depends on
/// <c>IResearchStorage</c>, never on a concrete platform class.
/// </para>
/// <para>
/// A fresh repo opts in by adding a <c>research:</c> block to
/// <c>.polyphony-config/profile.yaml</c>; no code changes are required.
/// The factory resolves the correct implementation from config.
/// </para>
/// </remarks>
public interface IResearchStorage
{
    /// <summary>
    /// The validated research target (repo, branch, platform) this storage
    /// instance is bound to.
    /// </summary>
    ResearchTarget Target { get; }
}
