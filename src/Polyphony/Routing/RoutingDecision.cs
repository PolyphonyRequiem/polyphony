namespace Polyphony.Routing;

/// <summary>
/// The result of phase detection: which SDLC phase a work item is in
/// and what action the conductor should take next.
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>
    /// The detected SDLC phase. One of the <see cref="SdlcPhase"/> constants.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// The recommended action. One of the <see cref="SdlcAction"/> constants.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Optional human-readable message explaining the routing decision.
    /// </summary>
    public string? Message { get; init; }
}
