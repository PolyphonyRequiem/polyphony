namespace Polyphony.Routing;

/// <summary>
/// Discriminated union representing the result of a lifecycle event transition validation.
/// <see cref="ValidTransition"/> carries a non-nullable <see cref="ValidTransition.TargetState"/>;
/// <see cref="NoOpTransition"/> carries the same target state but signals "already there —
/// idempotent no-op" so callers can succeed without re-applying the transition;
/// <see cref="InvalidTransition"/> allows a nullable <see cref="InvalidTransition.TargetState"/>
/// (null when the event or work item type is unrecognized).
/// </summary>
public union TransitionOutcome(ValidTransition, NoOpTransition, InvalidTransition);

/// <summary>The transition is legal — target state is always known.</summary>
public sealed record ValidTransition(int WorkItemId, string Event, string TargetState, string Message);

/// <summary>
/// The item is already in <see cref="TargetState"/>; the event is a structural no-op
/// (idempotent re-fire). Callers should treat this as success and skip applying the
/// transition. AB#3170: prevents <c>close_mark_satisfied</c> and similar
/// terminal-event sites from failing on replay.
/// </summary>
public sealed record NoOpTransition(int WorkItemId, string Event, string TargetState, string Message);

/// <summary>The transition is illegal — target state may be unknown.</summary>
public sealed record InvalidTransition(int WorkItemId, string Event, string? TargetState, string Message);
