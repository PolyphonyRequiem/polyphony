using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset;

public sealed class ProjectionResetExecutor(
    IJournalStore store,
    JournalDriftAnalyzer driftAnalyzer,
    ProjectionResetCoverageAnalyzer coverageAnalyzer,
    ProjectionResetPlanner planner,
    IEnumerable<IResourceDeleter> deleters)
{
    private readonly IJournalStore _store = store;
    private readonly JournalDriftAnalyzer _driftAnalyzer = driftAnalyzer;
    private readonly ProjectionResetCoverageAnalyzer _coverageAnalyzer = coverageAnalyzer;
    private readonly ProjectionResetPlanner _planner = planner;
    private readonly IReadOnlyDictionary<string, IResourceDeleter> _deleters = deleters
        .ToDictionary(deleter => deleter.Kind, StringComparer.Ordinal);

    public async Task<ResetApexResult> ExecuteAsync(
        int rootId,
        ProjectionResetExecutionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entries = await _store.QueryAsync(new JournalQuery { RootId = rootId }, ct).ConfigureAwait(false);
        var coverage = _coverageAnalyzer.Analyze(entries);
        var preAnalysis = await _driftAnalyzer.AnalyzeAsync(rootId, entries, ct).ConfigureAwait(false);
        var includeUnobserved = options.AllowUnjournaled
            && !string.Equals(coverage.Coverage, ProjectionResetCoverage.Complete, StringComparison.Ordinal);
        var plan = _planner.BuildPlan(preAnalysis.CurrentExpectedState, preAnalysis.ObservedResources, includeUnobserved, options.ForceMutated);

        if (!options.AllowUnjournaled
            && !string.Equals(coverage.Coverage, ProjectionResetCoverage.Complete, StringComparison.Ordinal))
        {
            return CreateResult(
                rootId,
                options,
                coverage.Coverage,
                attemptedTargets: [],
                deletedTargets: [],
                failedTargets: [],
                remainingResetTargets: DescribeRemainingTargets(preAnalysis, plan, includeUnobserved),
                blockedMutatedTargets: DescribeTargets(plan.BlockedMutatedTargets),
                stepsCompleted: [],
                stepsFailed: CompletePhaseNames(options.SkipState),
                error: BuildCoverageError(coverage));
        }

        var attemptedTargets = DescribeTargets(plan.AttemptedTargets);
        var deletedTargets = new List<ResetTargetDescriptor>();
        var failedTargets = new List<FailedResetTargetDescriptor>();
        var stepsCompleted = new List<string>();
        var stepsFailed = new List<string>();
        string? fatalError = null;

        try
        {
            foreach (var phase in ProjectionResetCatalog.OrderedPhases)
            {
                if (options.SkipState && string.Equals(phase.Name, "state", StringComparison.Ordinal))
                {
                    continue;
                }

                var phaseTargets = plan.AttemptedTargets.Where(target => phase.Matches(target.Resource)).ToArray();
                if (options.Execute)
                {
                    foreach (var target in phaseTargets)
                    {
                        if (!_deleters.TryGetValue(target.Kind, out var deleter))
                        {
                            failedTargets.Add(ProjectionResetCatalog.ToFailedDescriptor(target.Resource, $"No resource deleter is registered for kind '{target.Kind}'."));
                            continue;
                        }

                        var outcome = await deleter.DeleteAsync(
                            target.Resource,
                            target.Observation,
                            new ResourceDeletionContext
                            {
                                RootId = rootId,
                                Comment = options.Comment,
                            },
                            ct).ConfigureAwait(false);
                        if (!outcome.Success)
                        {
                            failedTargets.Add(ProjectionResetCatalog.ToFailedDescriptor(
                                target.Resource,
                                outcome.Error ?? $"Delete failed for {target.Kind}:{target.Id}."));
                            continue;
                        }

                        if (outcome.Deleted)
                        {
                            deletedTargets.Add(ProjectionResetCatalog.ToDescriptor(target.Resource));
                        }
                    }

                    if (string.Equals(phase.Name, "worktrees", StringComparison.Ordinal))
                    {
                        CleanupEmptyWorktreeRoots(phaseTargets.Select(target => target.Resource.Id));
                    }
                }

                stepsCompleted.Add(phase.Name);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            fatalError = ex.Message;
            if (stepsCompleted.Count < ProjectionResetCatalog.OrderedPhases.Count)
            {
                var failedPhase = ProjectionResetCatalog.OrderedPhases
                    .Skip(stepsCompleted.Count)
                    .FirstOrDefault(phase => !(options.SkipState && string.Equals(phase.Name, "state", StringComparison.Ordinal)));
                if (failedPhase is not null)
                {
                    stepsFailed.Add(failedPhase.Name);
                }
            }
        }

        var postAnalysis = await _driftAnalyzer.AnalyzeAsync(rootId, entries, ct).ConfigureAwait(false);
        var blockedMutatedTargets = options.ForceMutated ? [] : DescribeTargets(plan.BlockedMutatedTargets);
        var remainingResetTargets = DescribeRemainingTargets(postAnalysis, plan, includeUnobserved);
        var error = fatalError
            ?? (!options.ForceMutated && blockedMutatedTargets.Count > 0
                ? "Projection reset blocked on externally mutated resources. Re-run with --force-mutated to delete them anyway."
                : failedTargets.Count > 0
                    ? $"Projection reset failed for {failedTargets.Count} target(s)."
                    : options.Execute && remainingResetTargets.Count > 0
                        ? $"Projection reset verification found {remainingResetTargets.Count} remaining target(s)."
                        : null);
        return CreateResult(
            rootId,
            options,
            coverage.Coverage,
            attemptedTargets,
            deletedTargets,
            failedTargets,
            remainingResetTargets,
            blockedMutatedTargets,
            stepsCompleted,
            stepsFailed,
            error);
    }

    private static ResetApexResult CreateResult(
        int rootId,
        ProjectionResetExecutionOptions options,
        string coverage,
        IReadOnlyList<ResetTargetDescriptor> attemptedTargets,
        IReadOnlyList<ResetTargetDescriptor> deletedTargets,
        IReadOnlyList<FailedResetTargetDescriptor> failedTargets,
        IReadOnlyList<ResetTargetDescriptor> remainingResetTargets,
        IReadOnlyList<ResetTargetDescriptor> blockedMutatedTargets,
        IReadOnlyList<string> stepsCompleted,
        IReadOnlyList<string> stepsFailed,
        string? error)
        => new()
        {
            Root = rootId,
            Success = string.IsNullOrWhiteSpace(error),
            DryRun = !options.Execute,
            Strategy = ProjectionResetStrategy.Projection,
            Coverage = coverage,
            FallbackUsed = false,
            AttemptedTargets = attemptedTargets,
            DeletedTargets = deletedTargets,
            FailedTargets = failedTargets,
            RemainingResetTargets = remainingResetTargets,
            BlockedMutatedTargets = blockedMutatedTargets,
            StepsCompleted = stepsCompleted,
            StepsFailed = stepsFailed,
            StateSkipped = options.SkipState,
            Error = error,
        };

    private static IReadOnlyList<ResetTargetDescriptor> DescribeTargets(IEnumerable<ResetExecutionTarget> targets)
        => targets
            .Select(target => ProjectionResetCatalog.ToDescriptor(target.Resource))
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.Id, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<ResetTargetDescriptor> DescribeRemainingTargets(
        JournalDriftAnalysis analysis,
        ProjectionResetPlan plan,
        bool includeUnobserved)
    {
        var remaining = analysis.ResetTargets.Resources
            .Select(ProjectionResetCatalog.ToDescriptor)
            .ToList();
        if (includeUnobserved)
        {
            remaining.AddRange(plan.ResetTargets
                .Where(target => target.Observation is null)
                .Select(target => ProjectionResetCatalog.ToDescriptor(target.Resource)));
        }

        return remaining
            .GroupBy(target => (target.Kind, target.Id, target.Operation))
            .Select(group => group.First())
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> CompletePhaseNames(bool skipState)
        => ProjectionResetCatalog.OrderedPhases
            .Where(phase => !skipState || !string.Equals(phase.Name, "state", StringComparison.Ordinal))
            .Select(phase => phase.Name)
            .ToArray();

    private static string BuildCoverageError(ProjectionResetCoverageReport coverage)
    {
        var parts = new List<string>();
        if (coverage.MissingEffectsActions.Count > 0)
        {
            parts.Add($"missing journal effects for actions: {string.Join(", ", coverage.MissingEffectsActions)}");
        }

        if (coverage.MissingObservers.Count > 0)
        {
            parts.Add($"missing observers for kinds: {string.Join(", ", coverage.MissingObservers)}");
        }

        if (coverage.MissingDeleters.Count > 0)
        {
            parts.Add($"missing deleters for kinds: {string.Join(", ", coverage.MissingDeleters)}");
        }

        return parts.Count == 0
            ? "Projection reset coverage is incomplete. Re-run with --allow-unjournaled to proceed."
            : $"Projection reset coverage is incomplete: {string.Join("; ", parts)}. Re-run with --allow-unjournaled to proceed.";
    }

    private static void CleanupEmptyWorktreeRoots(IEnumerable<string> worktreePaths)
    {
        foreach (var rootPath in worktreePaths
                     .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteEmptyDirectoryTree(rootPath!);
        }
    }

    private static void TryDeleteEmptyDirectoryTree(string path)
    {
        var current = new DirectoryInfo(path);
        while (current.Exists)
        {
            if (current.EnumerateFileSystemInfos().Any())
            {
                return;
            }

            current.Delete();
            if (current.Name.StartsWith("root-", StringComparison.Ordinal))
            {
                return;
            }

            current = current.Parent;
            if (current is null)
            {
                return;
            }
        }
    }
}

public sealed record ProjectionResetExecutionOptions
{
    public bool Execute { get; init; }
    public bool AllowUnjournaled { get; init; }
    public bool ForceMutated { get; init; }
    public bool SkipState { get; init; }
    public string Comment { get; init; } = string.Empty;
}

public static class ProjectionResetStrategy
{
    public const string Projection = "projection";
    public const string Pattern = "pattern";
}
