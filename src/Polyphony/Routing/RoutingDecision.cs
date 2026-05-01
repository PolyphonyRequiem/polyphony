namespace Polyphony.Routing;

/// <summary>
/// Discriminated union representing the result of phase detection.
/// Each case encodes the SDLC phase and recommended action,
/// carrying only a human-readable message.
/// </summary>
public union RoutingDecision(
    NeedsPlanning,
    NeedsSeeding,
    ReadyForImplementation,
    ImplementationInProgress,
    ReadyForCompletion,
    RoutingDone,
    RoutingRemoved,
    RoutingUnknown);

/// <summary>Work item needs planning (Proposed state, plannable type).</summary>
public sealed record NeedsPlanning(string Message);

/// <summary>Work item needs seeding/decomposition (in progress, plannable-only, no children).</summary>
public sealed record NeedsSeeding(string Message);

/// <summary>Work item is ready for implementation.</summary>
public sealed record ReadyForImplementation(string Message);

/// <summary>Implementation is actively in progress.</summary>
public sealed record ImplementationInProgress(string Message);

/// <summary>All children complete — ready for close-out.</summary>
public sealed record ReadyForCompletion(string Message);

/// <summary>Work item is complete (terminal state).</summary>
public sealed record RoutingDone(string Message);

/// <summary>Work item has been removed (terminal state).</summary>
public sealed record RoutingRemoved(string Message);

/// <summary>Work item is in an unrecognized state or has unknown capabilities.</summary>
public sealed record RoutingUnknown(string Message);
