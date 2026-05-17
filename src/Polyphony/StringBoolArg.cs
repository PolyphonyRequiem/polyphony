namespace Polyphony;

/// <summary>
/// Shim helper for verbs that need to accept a bool flag from a workflow
/// YAML in the explicit-value form (e.g. <c>--delete-branch false</c>).
///
/// <para>
/// ConsoleAppFramework treats a <see cref="bool"/> parameter as a no-value
/// switch: <c>--flag</c> means <c>true</c>, <c>--flag false</c> /
/// <c>--flag=true</c> are rejected at parse time with
/// <c>"Argument 'false' is not recognized."</c>. Workflows that pass an
/// explicit value run aground, the dispatcher returns exit 1 with an
/// "unknown verb" error envelope, and the workflow node silently routes
/// past it (the original CAF-bool wiring bug class; see PR #451 for the
/// first instance, AB#3217 for the systemic write-up).
/// </para>
///
/// <para>
/// The workaround is to declare the verb's parameter as
/// <c>string flag = "true"</c> and call <see cref="Parse"/> early — after
/// <c>HaltIfMissing</c>, before any work — to convert it into a real
/// <see cref="bool"/>. On parse failure the helper emits the standard
/// routing-style error envelope via
/// <see cref="RequiredInput.EmitDispatchErrorEnvelope"/> and returns
/// <see langword="null"/>; callers should treat <see langword="null"/> as
/// "halt, envelope already emitted" and return
/// <see cref="ExitCodes.RoutingFailure"/> (matching
/// <see cref="RequiredInput.HaltIfMissing"/>'s convention for
/// dispatcher-level argument failures).
/// </para>
///
/// <para>
/// Accepted values (case-insensitive, no surrounding whitespace allowed):
/// <c>"true"</c>, <c>"false"</c>. Empty string and the literal
/// <c>"missing"</c> sentinel are NOT accepted here — required-ness is the
/// concern of <see cref="RequiredInput.HaltIfMissing"/> and the verb's
/// own default. Whitespace-only is treated as a parse error rather than
/// silently coerced to the default; workflows should omit the flag if
/// they want the default.
/// </para>
///
/// <para>
/// Long-term, ConsoleAppFramework gains real <c>--no-flag</c> negation
/// (tracked upstream); this helper goes away then. Until that lands,
/// every CAF <c>bool</c> parameter that a workflow YAML wants to set
/// explicitly false must use this shim. The lint at
/// <c>tests/lint-caf-bool-value-form.ps1</c> enforces that
/// workflow YAMLs only pair <c>--flag</c> with <c>true</c>/<c>false</c>
/// when the underlying verb is on the shim's allowlist.
/// </para>
/// </summary>
public static class StringBoolArg
{
    /// <summary>
    /// Parse a string flag value into a bool. On failure, emits the
    /// dispatch error envelope to stdout (caller should return a non-zero
    /// exit code) and returns <see langword="null"/>. On success, returns
    /// the parsed bool.
    /// </summary>
    /// <param name="verb">
    /// The verb name (e.g. <c>"pr merge-impl-pr"</c>) — surfaces in the
    /// error envelope's <c>verb</c> field so conductor / operators can
    /// identify the failing call site.
    /// </param>
    /// <param name="flag">
    /// The flag name including the leading dashes (e.g.
    /// <c>"--delete-branch"</c>) — surfaces in the human-readable error
    /// message and (for the <c>missing_args</c> envelope field) lets
    /// downstream tooling route on the specific flag.
    /// </param>
    /// <param name="raw">The raw string value the CAF dispatcher handed us.</param>
    /// <returns>
    /// The parsed bool, or <see langword="null"/> if <paramref name="raw"/>
    /// is not a recognised bool literal. Callers MUST check for null and
    /// return <see cref="ExitCodes.RoutingFailure"/> without emitting any
    /// further output (the envelope is already on stdout).
    /// </returns>
    public static bool? Parse(string verb, string flag, string raw)
    {
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;

        var quoted = raw is null ? "<null>" : $"'{raw}'";
        var error = $"polyphony {verb}: {flag} expects 'true' or 'false' (got {quoted}).";
        RequiredInput.EmitDispatchErrorEnvelope(verb, error, [flag]);
        return null;
    }
}
