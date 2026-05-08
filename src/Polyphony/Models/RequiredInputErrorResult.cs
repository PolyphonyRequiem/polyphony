namespace Polyphony;

/// <summary>
/// Routing-style envelope emitted on stdout when a verb (or the CLI itself) is invoked
/// with a missing required argument or an unrecognized verb/flag. Move #2 contract:
/// required-ness fails loud at every layer — the conductor sees a parseable JSON
/// envelope on stdout with <c>action == "error"</c> instead of stderr noise plus an
/// empty stdout (which conductor used to mistake for a silent success).
/// </summary>
/// <remarks>
/// <para>The shape is deliberately distinct from any per-verb result type so that
/// consumers can detect the "polyphony refused to even try" failure mode by branching
/// on <c>action == "error"</c> + the presence of <c>missing_args</c> /
/// <c>verb == ""</c>. Per-verb error envelopes (e.g. <c>BranchEnsurePlanResult</c> with
/// <c>action: error</c>) cover failures inside the verb body once arg parsing succeeds;
/// this envelope covers the strictly-prior contract.</para>
/// </remarks>
public sealed record RequiredInputErrorResult
{
    /// <summary>Always <c>"error"</c> — the routing-style action signal.</summary>
    public required string Action { get; init; }

    /// <summary>
    /// The verb invocation as the operator would type it (e.g. <c>"branch ensure-plan"</c>),
    /// or empty string when the failure is at the CLI dispatcher (unknown verb).
    /// </summary>
    public required string Verb { get; init; }

    /// <summary>Human-readable error message naming the missing/unknown args.</summary>
    public required string Error { get; init; }

    /// <summary>Machine-readable list of the missing required flag names (e.g. <c>["--root-id","--item-id"]</c>).</summary>
    public required string[] MissingArgs { get; init; }
}
