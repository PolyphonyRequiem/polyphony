using Polyphony.Infrastructure.Paths;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class ManifestFileDeleter(PolyphonyStatePaths statePaths) : IResourceDeleter
{
    private readonly PolyphonyStatePaths _statePaths = statePaths;

    public string Kind => ResourceKind.ManifestFile;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var path = await ResolvePathAsync(resource, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ResourceDeleteOutcome { Success = true, Deleted = false };
        }

        File.Delete(path);
        return new ResourceDeleteOutcome { Success = true, Deleted = true };
    }

    private async Task<string?> ResolvePathAsync(ProjectedResourceState resource, CancellationToken ct)
    {
        if (!resource.Id.StartsWith("manifest:", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(resource.Id);
        }

        return int.TryParse(resource.Id["manifest:".Length..], out var rootId)
            ? await _statePaths.GetManifestPathAsync(rootId, ct).ConfigureAwait(false)
            : null;
    }
}
