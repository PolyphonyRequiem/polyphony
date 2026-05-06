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

    /// <summary>
    /// Strict ordinal rank for threshold comparisons:
    /// <c>Needed=0 &lt; Ready=1 &lt; Fulfilling=2 &lt; Satisfied=3</c>.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="RequirementSetReducer"/> to evaluate whether a current
    /// disposition meets a <see cref="RequirementEdge.RequiredDisposition"/>
    /// threshold (current's order &gt;= threshold's order). Fails closed on
    /// unknown input rather than returning a default; callers that accept
    /// untrusted input must validate via <see cref="IsValid"/> first.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="disposition"/> is not one of the four canonical strings.
    /// </exception>
    public static int Order(string disposition) => disposition switch
    {
        Needed => 0,
        Ready => 1,
        Fulfilling => 2,
        Satisfied => 3,
        _ => throw new ArgumentOutOfRangeException(
            nameof(disposition), disposition, "Unknown disposition."),
    };

    /// <summary>
    /// Returns true when <paramref name="current"/> meets or exceeds the
    /// <paramref name="threshold"/> in the disposition ordering.
    /// </summary>
    public static bool Meets(string current, string threshold) =>
        Order(current) >= Order(threshold);
}
