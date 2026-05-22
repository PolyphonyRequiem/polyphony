using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class GitWorktreeDeleter(IGitClient git) : IResourceDeleter
{
    private readonly IGitClient _git = git;

    public string Kind => ResourceKind.GitWorktree;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var path = Path.GetFullPath(resource.Id);
        if (!Directory.Exists(path))
        {
            return new ResourceDeleteOutcome { Success = true, Deleted = false };
        }

        var result = await RemoveWithRetryAsync(path, ct).ConfigureAwait(false);
        return new ResourceDeleteOutcome
        {
            Success = result.Succeeded,
            Deleted = result.Succeeded,
            Error = result.Succeeded ? null : FormatError(result),
        };
    }

    private async Task<ProcessResult> RemoveWithRetryAsync(string path, CancellationToken ct)
    {
        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
        };

        ProcessResult? last = null;
        foreach (var delay in delays)
        {
            ct.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            last = await _git.WorktreeRemoveAsync(path, force: true, ct).ConfigureAwait(false);
            if (last.Succeeded || !Directory.Exists(path))
            {
                return last.Succeeded ? last : new ProcessResult(0, string.Empty, string.Empty);
            }
        }

        return last ?? new ProcessResult(1, string.Empty, "git worktree remove was not attempted");
    }

    private static string FormatError(ProcessResult result)
        => !string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stderr.Trim()
            : string.IsNullOrWhiteSpace(result.Stdout)
                ? $"exit {result.ExitCode}"
                : result.Stdout.Trim();
}
