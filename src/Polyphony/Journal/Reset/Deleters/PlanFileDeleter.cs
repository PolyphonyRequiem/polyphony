using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class PlanFileDeleter : IResourceDeleter
{
    public string Kind => ResourceKind.PlanFile;

    public Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var path = Path.GetFullPath(resource.Id);
        if (!File.Exists(path))
        {
            return Task.FromResult(new ResourceDeleteOutcome { Success = true, Deleted = false });
        }

        File.Delete(path);
        return Task.FromResult(new ResourceDeleteOutcome { Success = true, Deleted = true });
    }
}
