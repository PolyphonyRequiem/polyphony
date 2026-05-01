namespace Polyphony.Routing;

/// <summary>
/// Temporary compatibility bridge that decomposes a <see cref="RoutingDecision"/> DU
/// into its phase, action, and message components. This mapper is consumed by
/// <see cref="Commands.RouteCommand"/> and tests until all consumers migrate
/// to direct pattern matching (Task 2802).
/// </summary>
public static class RoutingDecisionMapper
{
    public static (string Phase, string Action, string? Message) ToComponents(RoutingDecision decision)
        => decision switch
        {
            NeedsPlanning d            => (SdlcPhase.NeedsPlanning, SdlcAction.Plan, d.Message),
            NeedsSeeding d             => (SdlcPhase.NeedsSeeding, SdlcAction.Seed, d.Message),
            ReadyForImplementation d   => (SdlcPhase.ReadyForImplementation, SdlcAction.Implement, d.Message),
            ImplementationInProgress d => (SdlcPhase.InProgress, SdlcAction.Monitor, d.Message),
            ReadyForCompletion d       => (SdlcPhase.ReadyForCompletion, SdlcAction.Close, d.Message),
            RoutingDone d              => (SdlcPhase.Done, SdlcAction.None, d.Message),
            RoutingRemoved d           => (SdlcPhase.Removed, SdlcAction.None, d.Message),
            RoutingUnknown d           => (SdlcPhase.Unknown, SdlcAction.None, d.Message),
            null                       => throw new ArgumentNullException(nameof(decision)),
        };
}
