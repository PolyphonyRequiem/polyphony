using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset;

public sealed class ProjectionResetPlanner
{
    public ProjectionResetPlan BuildPlan(
        CurrentExpectedStateResult currentExpectedState,
        IReadOnlyList<ObservedResourceState> observations,
        bool includeUnobservedResources,
        bool forceMutated)
    {
        ArgumentNullException.ThrowIfNull(currentExpectedState);
        ArgumentNullException.ThrowIfNull(observations);

        var observationByKey = observations.ToDictionary(
            observation => new ResourceKey { Kind = observation.Kind, Id = observation.Id },
            observation => observation);
        var resetTargets = new List<ResetExecutionTarget>();
        var attemptedTargets = new List<ResetExecutionTarget>();
        var blockedMutatedTargets = new List<ResetExecutionTarget>();

        foreach (var resource in currentExpectedState.Resources
                     .Where(resource => resource.PolyphonyOwned)
                     .Where(ProjectionResetCatalog.IsResetRelevant)
                     .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
                     .ThenBy(resource => resource.Id, StringComparer.Ordinal))
        {
            var hasObservation = observationByKey.TryGetValue(resource.Key, out var observation);
            if (!hasObservation)
            {
                if (!includeUnobservedResources)
                {
                    continue;
                }

                var blindTarget = new ResetExecutionTarget(resource, null);
                resetTargets.Add(blindTarget);
                attemptedTargets.Add(blindTarget);
                continue;
            }

            if (!observation!.Exists)
            {
                continue;
            }

            var target = new ResetExecutionTarget(resource, observation);
            resetTargets.Add(target);
            if (!observation.MatchesExpectedState && !forceMutated)
            {
                blockedMutatedTargets.Add(target);
                continue;
            }

            attemptedTargets.Add(target);
        }

        return new ProjectionResetPlan
        {
            ResetTargets = resetTargets,
            AttemptedTargets = attemptedTargets,
            BlockedMutatedTargets = blockedMutatedTargets,
        };
    }
}

public sealed record ProjectionResetPlan
{
    public required IReadOnlyList<ResetExecutionTarget> ResetTargets { get; init; }
    public required IReadOnlyList<ResetExecutionTarget> AttemptedTargets { get; init; }
    public required IReadOnlyList<ResetExecutionTarget> BlockedMutatedTargets { get; init; }
}

public sealed record ResetExecutionTarget(ProjectedResourceState Resource, ObservedResourceState? Observation)
{
    public string Kind => Resource.Kind;
    public string Id => Resource.Id;
}
