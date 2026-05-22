using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Infrastructure.Worktrees;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public sealed class GitWorktreeObserver(IGitClient git) : IResourceObserver
{
    private readonly IGitClient _git = git;

    public string Kind => ResourceKind.GitWorktree;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var worktreeList = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
        if (!worktreeList.Succeeded)
        {
            var error = !string.IsNullOrWhiteSpace(worktreeList.Stderr)
                ? worktreeList.Stderr.Trim()
                : worktreeList.Stdout.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(error)
                ? "git worktree list --porcelain failed"
                : error);
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var entries = WorktreeCommands.ParsePorcelain(worktreeList.Stdout);
        var byPath = entries
            .Select(entry => (Key: NormalizePath(entry.Path), Entry: entry))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Entry, comparer);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var observations = new List<ObservedResourceState>();
        foreach (var resource in expected)
        {
            var path = NormalizePath(resource.Id);
            var exists = byPath.TryGetValue(path, out var entry);
            var expectedBranch = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "branch");
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = exists,
                MatchesExpectedState = resource.Intent == ResourceIntent.EnsureAbsent
                    ? !exists
                    : exists && (string.IsNullOrWhiteSpace(expectedBranch)
                        || string.Equals(entry!.Branch, expectedBranch, StringComparison.Ordinal)),
                ActualState = exists ? entry!.Branch ?? "present" : "missing",
                ActualAttributes = exists
                    ? ResourceObserverSupport.CreateActualAttributes(("branch", entry!.Branch), ("head", entry.Head), ("is_detached", entry.IsDetached))
                    : null,
            });
        }

        var discovered = new List<DiscoveredResourceState>();
        var expectedIds = expected.Select(resource => NormalizePath(resource.Id)).ToHashSet(comparer);
        var commonDir = await _git.GetCommonDirAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(commonDir))
        {
            var (runsRoot, _) = RunsRootResolver.Resolve(commonDir);
            var rootRunsRoot = Path.Combine(runsRoot, $"root-{request.RootId}");
            foreach (var entry in entries)
            {
                var candidate = NormalizePath(entry.Path);
                if (expectedIds.Contains(candidate) || !IsSameOrSubpath(rootRunsRoot, candidate))
                {
                    continue;
                }

                discovered.Add(new DiscoveredResourceState
                {
                    Kind = Kind,
                    Id = entry.Path,
                    MatchesPolyphonyPattern = true,
                    ActualState = entry.Branch ?? "present",
                    ActualAttributes = ResourceObserverSupport.CreateActualAttributes(("branch", entry.Branch), ("head", entry.Head), ("is_detached", entry.IsDetached)),
                });
            }
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, comparer).ToArray(),
            DiscoveredResources = discovered.OrderBy(resource => resource.Id, comparer).ToArray(),
        };
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrSubpath(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = NormalizePath(root);
        var normalizedCandidate = NormalizePath(candidate);
        return string.Equals(normalizedRoot, normalizedCandidate, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
