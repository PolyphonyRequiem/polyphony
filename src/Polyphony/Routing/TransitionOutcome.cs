namespace Polyphony.Routing;

/// <summary>
/// Discriminated union representing the result of a lifecycle event transition validation.
/// <see cref="ValidTransition"/> carries a non-nullable <see cref="ValidTransition.TargetState"/>;
/// <see cref="InvalidTransition"/> allows a nullable <see cref="InvalidTransition.TargetState"/>
/// (null when the event or work item type is unrecognized).
/// </summary>
public union TransitionOutcome(ValidTransition, InvalidTransition);

/// <summary>The transition is legal — target state is always known.</summary>
public sealed record ValidTransition(int WorkItemId, string Event, string TargetState, string Message);

/// <summary>The transition is illegal — target state may be unknown.</summary>
public sealed record InvalidTransition(int WorkItemId, string Event, string? TargetState, string Message);
