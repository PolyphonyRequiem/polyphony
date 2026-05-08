using System.Text.Json;

namespace Polyphony;

/// <summary>
/// Sentinel values + envelope helper for the Move #2 "required-ness fails loud"
/// contract. Verbs that previously declared required positional/flag parameters
/// (which ConsoleAppFramework would reject at parse time with stderr noise +
/// non-zero exit, leaving conductor to mistake the empty stdout for silent
/// success) now declare them as optional with a sentinel default and call
/// <see cref="HaltIfMissing"/> early in the verb body. On a missing required arg,
/// a routing envelope is emitted on stdout and <see cref="ExitCodes.RoutingFailure"/>
/// is returned — the workflow gate routes on <c>action == "error"</c>.
/// </summary>
/// <remarks>
/// Usage pattern:
/// <code>
/// public async Task&lt;int&gt; EnsurePlan(
///     int rootId = RequiredInput.MissingInt,
///     int itemId = RequiredInput.MissingInt,
///     int parentItemId = 0,
///     CancellationToken ct = default)
/// {
///     if (RequiredInput.HaltIfMissing("branch ensure-plan",
///         ("--root-id", rootId == RequiredInput.MissingInt),
///         ("--item-id", itemId == RequiredInput.MissingInt)) is { } halt)
///         return halt;
///     // ... real verb body
/// }
/// </code>
/// </remarks>
public static class RequiredInput
{
    /// <summary>
    /// Sentinel default for a required <c>int</c> parameter. Chosen at the far end
    /// of <see cref="int"/> so it cannot collide with any plausible work-item id,
    /// PR number, depth, or limit a workflow could legitimately pass.
    /// </summary>
    public const int MissingInt = int.MinValue;

    /// <summary>
    /// Returns <see cref="ExitCodes.RoutingFailure"/> (after emitting a routing
    /// envelope to stdout) when any of <paramref name="checks"/> reports a missing
    /// arg; returns <c>null</c> when all required args are present (the verb
    /// should continue with its real body).
    /// </summary>
    public static int? HaltIfMissing(string verb, params (string Flag, bool Missing)[] checks)
    {
        var missing = new List<string>();
        foreach (var (flag, isMissing) in checks)
        {
            if (isMissing) missing.Add(flag);
        }
        if (missing.Count == 0) return null;
        EmitMissingEnvelope(verb, missing);
        return ExitCodes.RoutingFailure;
    }

    /// <summary>
    /// Emits the routing-style envelope to stdout. Public so that the CLI-level
    /// unknown-verb / unknown-flag handler in <c>Program.cs</c> can reuse the
    /// exact same shape.
    /// </summary>
    public static void EmitMissingEnvelope(string verb, IReadOnlyList<string> missing)
    {
        var result = new RequiredInputErrorResult
        {
            Action = "error",
            Verb = verb,
            Error = string.IsNullOrEmpty(verb)
                ? $"polyphony: missing required argument(s): {string.Join(", ", missing)}"
                : $"polyphony {verb}: missing required argument(s): {string.Join(", ", missing)}",
            MissingArgs = [.. missing],
        };
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.RequiredInputErrorResult));
    }

    /// <summary>
    /// Emits a routing-style envelope for an arbitrary CLI dispatcher failure
    /// (unknown verb, unknown flag, parse error). Mirrors
    /// <see cref="EmitMissingEnvelope"/> but lets the caller supply a
    /// pre-composed error message rather than synthesising one from missing
    /// flags. Used by <c>Program.cs</c> when wrapping
    /// <see cref="ConsoleAppFramework"/> dispatch.
    /// </summary>
    public static void EmitDispatchErrorEnvelope(string verb, string error, IReadOnlyList<string>? missingArgs = null)
    {
        var result = new RequiredInputErrorResult
        {
            Action = "error",
            Verb = verb,
            Error = error,
            MissingArgs = missingArgs is null ? [] : [.. missingArgs],
        };
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.RequiredInputErrorResult));
    }
}
