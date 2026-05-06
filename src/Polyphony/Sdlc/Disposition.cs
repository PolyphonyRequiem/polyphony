namespace Polyphony.Sdlc;

/// <summary>
/// Disposition string constants used by the requirement model.
/// String constants (not enums) for AOT compatibility and forward extensibility.
/// </summary>
/// <remarks>
/// Lifecycle order: <c>Needed</c> → <c>Ready</c> → <c>Fulfilling</c> → <c>Satisfied</c>.
/// <list type="bullet">
///   <item><description><c>Needed</c>: prerequisites unmet; cannot start.</description></item>
///   <item><description><c>Ready</c>: prerequisites satisfied; can be dispatched.</description></item>
///   <item><description><c>Fulfilling</c>: actively being worked on.</description></item>
///   <item><description><c>Satisfied</c>: complete; downstream gates may release.</description></item>
/// </list>
/// </remarks>
public static class Disposition
{
    public const string Needed = "needed";
    public const string Ready = "ready";
    public const string Fulfilling = "fulfilling";
    public const string Satisfied = "satisfied";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the four canonical disposition strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Needed or Ready or Fulfilling or Satisfied;
}
