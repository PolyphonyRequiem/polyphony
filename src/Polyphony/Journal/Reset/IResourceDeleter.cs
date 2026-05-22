using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset;

public interface IResourceDeleter
{
    string Kind { get; }

    Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct);
}

public sealed record ResourceDeletionContext
{
    public required int RootId { get; init; }
    public string Comment { get; init; } = string.Empty;
}

public sealed record ResourceDeleteOutcome
{
    public required bool Success { get; init; }
    public bool Deleted { get; init; }
    public string? Error { get; init; }
}
