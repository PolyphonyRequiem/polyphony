namespace Polyphony.Research;

/// <summary>
/// Azure DevOps–backed research storage. Targets a research archive hosted
/// on ADO, identified by the <see cref="IResearchStorage.Target"/>.
/// </summary>
/// <remarks>
/// File-level operations (read, write, list) will be added when the
/// research-augmented agent PRs (Issues 2–4 on Epic #3107) land. Those
/// operations will use the ADO REST API, following the same pattern as
/// <see cref="Infrastructure.AzureDevOps.IAdoClient"/>.
/// </remarks>
public sealed class AdoResearchStorage(ResearchTarget target) : IResearchStorage
{
    /// <inheritdoc />
    public ResearchTarget Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
}
