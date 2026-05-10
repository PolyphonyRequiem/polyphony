namespace Polyphony;

using System.Text.Json.Serialization;

/// <summary>
/// Output of <c>polyphony plan extract-renegotiation-flag &lt;pr-number&gt;</c>.
/// Routing-style envelope (always exit 0): consumers branch on
/// <see cref="Success"/> + <see cref="ErrorCode"/>.
///
/// <para>Phase 3 P8 introduces the <c>requests-parent-change</c>
/// HTML-comment-fenced block convention in plan-PR bodies. The flag
/// declares "the scope I was given assumed something that turns out to
/// be wrong — please re-plan from a higher level." This verb extracts
/// the flag and the renegotiation reason so a downstream workflow
/// handler can re-enter parent planning. The handler itself ships in a
/// separate PR (<c>p3-renegotiation-handler</c>); this verb is the
/// mechanics-only half.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PlanExtractRenegotiationFlagResult
{
    /// <summary>True iff the verb completed without an unrecoverable error. Always emitted.</summary>
    public required bool Success { get; init; }

    /// <summary>PR number we inspected. Echoed for traceability.</summary>
    public required int PrNumber { get; init; }

    /// <summary>True iff at least one well-formed renegotiation block was extracted.</summary>
    public required bool FlagPresent { get; init; }

    /// <summary>
    /// Concatenated trimmed renegotiation reason(s), or null when no
    /// well-formed block was present. Multiple blocks are joined with a
    /// single blank line.
    ///
    /// <para>
    /// Always serialized to JSON (overrides the per-context
    /// <c>WhenWritingNull</c> default) so workflow Jinja consumers under
    /// <c>strict_undefined</c> can reference
    /// <c>output.renegotiation_request</c> unconditionally without
    /// raising on a missing dict key. Mirrors the
    /// <see cref="PlanDeriveAncestorChainResult.ParentItemId"/> precedent
    /// (Bug #8 fix, 2026-05-08).
    /// </para>
    /// <para>
    /// AB#3067 dogfood (2026-05-10) reproduced the same trap shape:
    /// <c>plan-level.yaml</c>'s output map referenced
    /// <c>extract_renegotiation_flag.output.renegotiation_request</c>
    /// guarded only by <c>extract_renegotiation_flag is defined</c>; the
    /// agent-level guard passed but the dict key was elided per
    /// <c>WhenWritingNull</c>, raising TemplateError. PR #263 tightened
    /// the YAML to a two-level guard (defense-in-depth at the workflow);
    /// this attribute closes the underlying source so the workflow guard
    /// is no longer load-bearing.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RenegotiationRequest { get; init; }

    /// <summary>
    /// True when either no fence was present at all, or every opening
    /// fence had a matching closer. False when at least one opener has
    /// no matching closer — the workflow should refuse to act on the
    /// extracted reason and surface this to a human.
    /// </summary>
    public required bool FencedBlockWellFormed { get; init; }

    /// <summary>
    /// Stable machine-readable error code, or null on success. Defined codes:
    /// <c>malformed_renegotiation_block</c>, <c>pr_not_found</c>,
    /// <c>repo_not_resolved</c>, <c>gh_failed</c>, <c>gh_timeout</c>,
    /// <c>config_error</c>.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error detail; null on success.</summary>
    public string? ErrorMessage { get; init; }
}
