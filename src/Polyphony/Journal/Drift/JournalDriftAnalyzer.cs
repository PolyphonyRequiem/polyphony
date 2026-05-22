using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Drift;

public sealed class JournalDriftAnalyzer(IEnumerable<IResourceObserver> observers)
{
    private readonly IReadOnlyDictionary<string, IResourceObserver> _observers = observers
        .ToDictionary(observer => observer.Kind, StringComparer.Ordinal);

    public async Task<JournalDriftAnalysis> AnalyzeAsync(int rootId, IReadOnlyList<JournalEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var currentExpectedState = CurrentExpectedState.Project(entries);
        var ownedResources = OwnedResources.Project(currentExpectedState);
        var findings = new List<DriftFinding>();
        var observedResources = new List<ObservedResourceState>();

        foreach (var group in currentExpectedState.Resources.GroupBy(resource => resource.Kind, StringComparer.Ordinal))
        {
            if (!_observers.TryGetValue(group.Key, out var observer) || !observer.CanObserve)
            {
                continue;
            }

            var batch = await observer.ObserveAsync(
                new ResourceObservationRequest
                {
                    RootId = rootId,
                    ExpectedResources = group.OrderBy(resource => resource.Id, StringComparer.Ordinal).ToArray(),
                },
                ct).ConfigureAwait(false);

            observedResources.AddRange(batch.Observations);
            findings.AddRange(FoldExpected(group.ToArray(), batch.Observations));
            findings.AddRange(FoldDiscovered(group.Key, group.ToArray(), batch.DiscoveredResources));
        }

        var resetTargets = ResetTargets.Project(currentExpectedState, observedResources);
        var orderedFindings = findings
            .OrderBy(finding => finding.Kind, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ThenBy(finding => finding.Classification, StringComparer.Ordinal)
            .ToArray();

        return new JournalDriftAnalysis
        {
            CurrentExpectedState = currentExpectedState,
            OwnedResources = ownedResources,
            ResetTargets = resetTargets,
            Result = new DriftResult
            {
                Status = "ok",
                RootId = rootId,
                Findings = orderedFindings,
                Summary = new DriftSummary
                {
                    Consistent = orderedFindings.Count(finding => string.Equals(finding.Classification, DriftClassifications.Consistent, StringComparison.Ordinal)),
                    ExternalDelete = orderedFindings.Count(finding => string.Equals(finding.Classification, DriftClassifications.ExternalDelete, StringComparison.Ordinal)),
                    ExternalMutation = orderedFindings.Count(finding => string.Equals(finding.Classification, DriftClassifications.ExternalMutation, StringComparison.Ordinal)),
                    ExternalCreate = orderedFindings.Count(finding => string.Equals(finding.Classification, DriftClassifications.ExternalCreatePolyphonyNamed, StringComparison.Ordinal)),
                },
            },
        };
    }

    private static IEnumerable<DriftFinding> FoldExpected(
        IReadOnlyList<ProjectedResourceState> expectedResources,
        IReadOnlyList<ObservedResourceState> observations)
    {
        var observedById = observations.ToDictionary(
            observation => new ResourceKey { Kind = observation.Kind, Id = observation.Id },
            observation => observation);

        foreach (var expected in expectedResources)
        {
            if (!observedById.TryGetValue(expected.Key, out var observation))
            {
                continue;
            }

            var classification = ClassifyExpected(expected, observation);
            if (classification is null)
            {
                continue;
            }

            yield return new DriftFinding
            {
                Kind = expected.Kind,
                Id = expected.Id,
                Classification = classification,
                ExpectedState = DescribeExpectedState(expected),
                ActualState = observation.ActualState,
                PolyphonyOwned = expected.PolyphonyOwned,
                Platform = expected.Platform,
                ParentId = expected.ParentId,
            };
        }
    }

    private static IEnumerable<DriftFinding> FoldDiscovered(
        string kind,
        IReadOnlyList<ProjectedResourceState> expectedResources,
        IReadOnlyList<DiscoveredResourceState> discoveredResources)
    {
        var expectedIds = expectedResources.Select(resource => resource.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var discovered in discoveredResources)
        {
            if (expectedIds.Contains(discovered.Id) || !discovered.MatchesPolyphonyPattern)
            {
                continue;
            }

            yield return new DriftFinding
            {
                Kind = kind,
                Id = discovered.Id,
                Classification = DriftClassifications.ExternalCreatePolyphonyNamed,
                ExpectedState = "untracked",
                ActualState = discovered.ActualState,
                PolyphonyOwned = false,
            };
        }
    }

    private static string? ClassifyExpected(ProjectedResourceState expected, ObservedResourceState observation)
    {
        if (!observation.Exists)
        {
            return ExpectsPresence(expected)
                ? DriftClassifications.ExternalDelete
                : DriftClassifications.Consistent;
        }

        if (observation.MatchesExpectedState)
        {
            return DriftClassifications.Consistent;
        }

        return DriftClassifications.ExternalMutation;
    }

    private static bool ExpectsPresence(ProjectedResourceState expected)
        => expected.Intent != ResourceIntent.EnsureAbsent;

    private static string DescribeExpectedState(ProjectedResourceState expected)
    {
        if (expected.Intent == ResourceIntent.EnsureAbsent)
        {
            return "absent";
        }

        var namedState = ResourceObserverSupport.GetStringAttribute(expected.Attributes, "target_state")
            ?? ResourceObserverSupport.GetStringAttribute(expected.Attributes, "state");
        if (!string.IsNullOrWhiteSpace(namedState))
        {
            return namedState;
        }

        var sha = ResourceObserverSupport.GetBranchExpectedSha(expected.Attributes);
        if (!string.IsNullOrWhiteSpace(sha))
        {
            return $"present@{sha}";
        }

        return expected.Intent switch
        {
            ResourceIntent.EnsurePresent => "present",
            ResourceIntent.AdvancePointer => "advanced",
            ResourceIntent.SetState => "updated",
            ResourceIntent.UpdateMetadata => "metadata_updated",
            ResourceIntent.Attach => "attached",
            ResourceIntent.Detach => "detached",
            ResourceIntent.Observe => "observed",
            _ => expected.Intent.ToString(),
        };
    }
}

public sealed record JournalDriftAnalysis
{
    public required CurrentExpectedStateResult CurrentExpectedState { get; init; }
    public required OwnedResourcesResult OwnedResources { get; init; }
    public required ResetTargetsResult ResetTargets { get; init; }
    public required DriftResult Result { get; init; }
}

public static class DriftClassifications
{
    public const string Consistent = "consistent";
    public const string ExternalDelete = "external_delete";
    public const string ExternalMutation = "external_mutation";
    public const string ExternalCreatePolyphonyNamed = "external_create_polyphony_named";
    public const string Unknown = "unknown";
}
