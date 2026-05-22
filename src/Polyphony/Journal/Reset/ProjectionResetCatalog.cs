using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Tagging;

namespace Polyphony.Journal.Reset;

[ExcludedFromProjectionReset(ResourceKind.GitTag, "Pattern reset never deletes git tags.")]
[ExcludedFromProjectionReset(ResourceKind.GitHubPrComment, "Pattern reset never deletes GitHub PR comments.")]
[ExcludedFromProjectionReset(ResourceKind.AdoPrComment, "Pattern reset never deletes ADO PR comments.")]
[ExcludedFromProjectionReset(ResourceKind.AdoPrVote, "Pattern reset never deletes ADO PR votes.")]
[ExcludedFromProjectionReset(ResourceKind.AdoWorkItem, "Pattern reset does not delete ADO work items.")]
[ExcludedFromProjectionReset(ResourceKind.AdoWorkItemState, "Pattern reset does not revert work-item state transitions.")]
public static class ProjectionResetCatalog
{
    public const string DeleteOperation = "delete";

    private static readonly ResetPhaseDefinition[] s_phases =
    [
        new("prs", static resource => resource.Kind is ResourceKind.GitHubPr or ResourceKind.AdoPr),
        new("worktrees", static resource => resource.Kind == ResourceKind.GitWorktree),
        new("branches", static resource => resource.Kind == ResourceKind.GitBranch),
        new("facets", static resource => resource.Kind == ResourceKind.AdoWorkItemTag && IsFacetResetTag(resource.Id)),
        new("manifest", static resource => resource.Kind is ResourceKind.ManifestFile or ResourceKind.PlanFile or ResourceKind.LockFile),
        new("state", static resource => resource.Kind == ResourceKind.AdoWorkItemTag && IsWatermarkTag(resource.Id)),
    ];

    public static IReadOnlyList<ResetPhaseDefinition> OrderedPhases => s_phases;

    public static IReadOnlyDictionary<string, string> ExcludedKinds => typeof(ProjectionResetCatalog)
        .GetCustomAttributes(typeof(ExcludedFromProjectionResetAttribute), inherit: false)
        .Cast<ExcludedFromProjectionResetAttribute>()
        .ToDictionary(attribute => attribute.Kind, attribute => attribute.Reason, StringComparer.Ordinal);

    public static bool IsResetRelevant(ProjectedResourceState resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return s_phases.Any(phase => phase.Matches(resource));
    }

    public static bool IsWatermarkTag(string resourceId)
        => TryGetTag(resourceId, out _, out var tag)
            && tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal);

    public static bool IsFacetResetTag(string resourceId)
        => TryGetTag(resourceId, out _, out var tag)
            && (string.Equals(tag, PolyphonyTags.Planned, StringComparison.Ordinal)
                || tag.StartsWith(PolyphonyTags.FacetsPrefix + "=", StringComparison.Ordinal));

    public static bool TryGetTag(string resourceId, out int workItemId, out string tag)
        => ResourceObserverSupport.TryParseWorkItemTagId(resourceId, out workItemId, out tag);

    public static ResetTargetDescriptor ToDescriptor(ProjectedResourceState resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new ResetTargetDescriptor
        {
            Kind = resource.Kind,
            Id = resource.Id,
            Operation = DeleteOperation,
        };
    }

    public static FailedResetTargetDescriptor ToFailedDescriptor(ProjectedResourceState resource, string error)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new FailedResetTargetDescriptor
        {
            Kind = resource.Kind,
            Id = resource.Id,
            Operation = DeleteOperation,
            Error = error,
        };
    }
}

public sealed record ResetPhaseDefinition(string Name, Func<ProjectedResourceState, bool> Matches);
