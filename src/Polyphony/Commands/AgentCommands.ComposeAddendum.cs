using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Guidance;
using Polyphony.Models;
using Polyphony.Policy;
using Polyphony.Sdlc;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony agent compose-addendum</c> — composes the
/// <see cref="AgentAddendum"/> the actionable (and eventually plannable /
/// implementable) workflow injects into an agent step's prompt context.
///
/// <para>Routing-style verb: ALWAYS exits <see cref="ExitCodes.Success"/>;
/// the workflow gates on the envelope's <c>error_code</c>. Errors that
/// prevent producing an addendum (work item missing, type unknown,
/// invalid facet-profile config, misconfigured guidance source) surface
/// as <c>error</c> + <c>error_code</c> in the same envelope.</para>
///
/// <para>Composition delegates to <see cref="FacetProfileComposer"/>:
/// skills + MCPs are unioned across the item's facets, deduped, and
/// sorted ascending under the ordinal comparer. Unknown facet names
/// (a typo on the item's type, or a facet without a bound profile) are
/// silently omitted by design — the load-time
/// <see cref="FacetProfileValidator"/> is the place where unknown-name
/// typos are rejected, not this verb.</para>
///
/// <para>Per-item guidance flows through
/// <see cref="GuidanceExtractor.Extract"/> using the
/// <see cref="PolicyResolver.ResolveGuidance"/> result for the work
/// item's type. Distinct from the addendum's deduped sets, guidance is
/// passed through verbatim and reflected on
/// <see cref="AgentComposeAddendumResult.Guidance"/>.</para>
/// </summary>
public sealed partial class AgentCommands
{
    /// <summary>
    /// Compose the agent addendum for <paramref name="workItem"/>.
    /// </summary>
    /// <param name="workItem">ADO work item id (positional, required).
    /// The verb looks up the item, reads its type's facet set, and
    /// composes the addendum from the bound facet profiles.</param>
    /// <param name="policy">Path to the policy file used for guidance
    /// resolution. Defaults to <c>.conductor/policy.yaml</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("compose-addendum")]
    [VerbResult(typeof(AgentComposeAddendumResult))]
    public async Task<int> ComposeAddendum(
        [Argument] int workItem,
        string policy = ".conductor/policy.yaml",
        CancellationToken ct = default)
    {
        if (workItem <= 0)
        {
            Emit(EmptyResult(workItem, "work_item_id must be positive", "invalid_argument"));
            return ExitCodes.Success;
        }

        Twig.Domain.Aggregates.WorkItem? item;
        try
        {
            item = await _repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Emit(EmptyResult(workItem, ex.Message, "cache_error"));
            return ExitCodes.Success;
        }

        if (item is null)
        {
            Emit(EmptyResult(workItem, $"Work item {workItem} not found.", "work_item_not_found"));
            return ExitCodes.Success;
        }

        var typeName = item.Type.Value ?? "";
        if (string.IsNullOrEmpty(typeName) ||
            !_processConfig.Types.TryGetValue(typeName, out var typeConfig))
        {
            Emit(EmptyResult(workItem,
                $"Type '{typeName}' (item {workItem}) not found in process config.",
                "type_unknown"));
            return ExitCodes.Success;
        }

        IReadOnlyDictionary<string, FacetProfile> profiles;
        try
        {
            profiles = _processConfig.GetFacetProfiles();
        }
        catch (Exception ex)
        {
            Emit(EmptyResult(workItem, ex.Message, "invalid_facet_profile_config"));
            return ExitCodes.Success;
        }

        // Per-item guidance — best effort; misconfigured policy surfaces
        // as guidance_misconfigured rather than a thrown exception so the
        // workflow can route on it the same way it routes on every other
        // error_code.
        PolicyConfig policyConfig;
        try
        {
            policyConfig = PolicyLoader.LoadOrDefault(policy);
        }
        catch (Exception ex)
        {
            Emit(EmptyResult(workItem, ex.Message, "guidance_misconfigured"));
            return ExitCodes.Success;
        }

        var resolvedGuidance = PolicyResolver.ResolveGuidance(policyConfig, scope: $"type:{typeName}");

        string? guidance;
        try
        {
            guidance = GuidanceExtractor.Extract(item, resolvedGuidance);
        }
        catch (ArgumentException ex)
        {
            Emit(EmptyResult(workItem, ex.Message, "guidance_misconfigured"));
            return ExitCodes.Success;
        }

        var addendum = FacetProfileComposer.Compose(typeConfig.Facets, profiles, guidance);

        var result = new AgentComposeAddendumResult
        {
            WorkItemId = workItem,
            Facets = typeConfig.Facets,
            Skills = addendum.Skills,
            Mcps = addendum.Mcps,
            Guidance = addendum.GuidanceContext,
            GuidancePresent = addendum.GuidanceContext is not null,
        };

        Emit(result);
        return ExitCodes.Success;
    }

    private static AgentComposeAddendumResult EmptyResult(int workItemId, string error, string errorCode) =>
        new()
        {
            WorkItemId = workItemId,
            Facets = Array.Empty<string>(),
            Skills = Array.Empty<string>(),
            Mcps = Array.Empty<string>(),
            Guidance = null,
            GuidancePresent = false,
            Error = error,
            ErrorCode = errorCode,
        };

    private static void Emit(AgentComposeAddendumResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.AgentComposeAddendumResult));
}
