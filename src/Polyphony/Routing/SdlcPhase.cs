namespace Polyphony.Routing;

/// <summary>
/// SDLC phase string constants used by the routing engine.
/// String constants (not enums) for AOT compatibility and forward extensibility.
/// </summary>
public static class SdlcPhase
{
    public const string NeedsPlanning = "needs_planning";
    public const string NeedsSeeding = "needs_seeding";
    public const string ReadyForImplementation = "ready_for_implementation";
    public const string InProgress = "in_progress";
    public const string ReadyForCompletion = "ready_for_completion";
    public const string Done = "done";
    public const string Removed = "removed";
    public const string Unknown = "unknown";
}
