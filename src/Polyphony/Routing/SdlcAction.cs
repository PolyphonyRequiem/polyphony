namespace Polyphony.Routing;

/// <summary>
/// SDLC action string constants used by the routing engine.
/// String constants (not enums) for AOT compatibility and forward extensibility.
/// </summary>
public static class SdlcAction
{
    public const string Plan = "plan";
    public const string Seed = "seed";
    public const string Implement = "implement";
    public const string Monitor = "monitor";
    public const string Close = "close";
    public const string None = "none";
}
