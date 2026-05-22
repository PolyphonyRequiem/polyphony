using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class GitBranchDeleter(IGitClient git) : IResourceDeleter
{
    private readonly IGitClient _git = git;

    public string Kind => ResourceKind.GitBranch;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var remotePresent = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "remote_sha") is { Length: > 0 };
        var localPresent = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "local_sha") is { Length: > 0 };
        if (!remotePresent && !localPresent && observation is not null)
        {
            return new ResourceDeleteOutcome { Success = true, Deleted = false };
        }

        var errors = new List<string>();
        var deleted = false;

        if (remotePresent || observation is null)
        {
            var remote = await _git.DeleteRemoteBranchAsync("origin", resource.Id, ct).ConfigureAwait(false);
            if (remote.Succeeded)
            {
                deleted = true;
            }
            else
            {
                errors.Add($"remote: {FormatError(remote)}");
            }
        }

        if (localPresent || observation is null)
        {
            var local = await _git.DeleteLocalBranchAsync(resource.Id, force: true, ct).ConfigureAwait(false);
            if (local.Succeeded)
            {
                deleted = true;
            }
            else
            {
                errors.Add($"local: {FormatError(local)}");
            }
        }

        return new ResourceDeleteOutcome
        {
            Success = errors.Count == 0,
            Deleted = deleted && errors.Count == 0,
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    private static string FormatError(ProcessResult result)
        => !string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stderr.Trim()
            : string.IsNullOrWhiteSpace(result.Stdout)
                ? $"exit {result.ExitCode}"
                : result.Stdout.Trim();
}
