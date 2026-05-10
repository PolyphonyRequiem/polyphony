namespace Polyphony.Policy;

/// <summary>
/// Root-level policy configuration loaded from <c>.polyphony-config/policy.yaml</c>.
/// Workflows call <c>polyphony policy load</c> at start-of-run and pass the
/// resolved JSON downstream; individual route conditions invoke
/// <c>polyphony policy resolve --scope &lt;…&gt; --domain &lt;…&gt;</c> for the
/// effective mode + caps at each decision point.
/// </summary>
public sealed class PolicyConfig
{
    /// <summary>Schema version. Currently 1; reserved for forward-compat.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Approval-gate policy (plan-approval, user-acceptance).</summary>
    public DomainPolicy? Approvals { get; set; }

    /// <summary>PR-merge policy (github-pr feature/PG, ado-pr stub).</summary>
    public DomainPolicy? Pr { get; set; }

    /// <summary>Open-questions policy (question loop gating during implementation).</summary>
    public DomainPolicy? OpenQuestions { get; set; }

    /// <summary>Concurrency knobs (orthogonal to mode/scope).</summary>
    public ConcurrencyPolicy? Concurrency { get; set; }

    /// <summary>
    /// Per-item guidance source-of-record (Phase 6 PR #6). Default is
    /// <see cref="Polyphony.Sdlc.GuidanceSource.DescriptionBlock"/>; opt into
    /// <see cref="Polyphony.Sdlc.GuidanceSource.AdoField"/> by setting
    /// <c>guidance.source</c> + <c>guidance.ado_field_name</c>.
    /// </summary>
    public GuidancePolicy? Guidance { get; set; }

    /// <summary>
    /// Root-fallback policy (Phase 1 root-fallback-gate). Controls how the
    /// shared <c>root-fallback-gate</c> sub-workflow behaves when invoked
    /// without a <c>root_id</c>. Default is <see cref="RootFallbackAutoDecide.Prompt"/>
    /// (surface a human gate); workspaces with a high-confidence policy can
    /// bypass the gate by setting <c>auto_decide</c> to
    /// <c>use_active_item</c> or <c>abort</c>.
    /// </summary>
    public RootFallbackPolicy? RootFallback { get; set; }

    /// <summary>
    /// Renegotiation bubble-up policy (Phase 7 apex-driver). Controls how
    /// the <c>apex-driver</c> workflow handles a child <c>plan-level</c>
    /// invocation that returns <c>renegotiation_pending=true</c>. Default
    /// is <see cref="RenegotiationAutoDecide.Prompt"/> (surface a human
    /// gate so the operator picks renegotiate / override / abort);
    /// high-confidence workspaces can opt into <c>auto_restart</c>
    /// (re-enter the parent's plan-level automatically) or <c>ignore</c>
    /// (treat the child plan as accepted and continue).
    /// </summary>
    public RenegotiationPolicy? Renegotiation { get; set; }
}

/// <summary>
/// Root-fallback policy. Drives the behavior of the shared
/// <c>root-fallback-gate.yaml</c> sub-workflow when a sub-workflow is
/// invoked without a root work-item id in its input.
/// </summary>
public sealed class RootFallbackPolicy
{
    /// <summary>
    /// Auto-decide policy. One of the <see cref="RootFallbackAutoDecide"/>
    /// constants. Null falls back to <see cref="RootFallbackAutoDecide.Prompt"/>
    /// via <see cref="PolicyLoader.ApplyBuiltInDefaults"/>.
    /// </summary>
    public string? AutoDecide { get; set; }
}

/// <summary>
/// Canonical string constants for <see cref="RootFallbackPolicy.AutoDecide"/>.
/// Mirrors <see cref="Polyphony.Sdlc.GuidanceSource"/>'s string-constant pattern
/// so the YAML deserializer accepts the literal token verbatim (no enum
/// naming-convention gymnastics).
/// </summary>
public static class RootFallbackAutoDecide
{
    /// <summary>Surface the root-fallback human gate (default).</summary>
    public const string Prompt = "prompt";

    /// <summary>Auto-resolve by treating the active work item as the root.</summary>
    public const string UseActiveItem = "use_active_item";

    /// <summary>Auto-resolve by aborting the workflow with an error.</summary>
    public const string Abort = "abort";

    /// <summary>True when <paramref name="value"/> is one of the canonical tokens.</summary>
    public static bool IsValid(string value) =>
        value is Prompt or UseActiveItem or Abort;
}

/// <summary>
/// Renegotiation bubble-up policy. Drives apex-driver behavior when a
/// child plan-level sub-workflow returns
/// <c>renegotiation_pending=true</c> — i.e. the child planner is asking
/// the parent to re-author its plan to accommodate the child.
/// </summary>
public sealed class RenegotiationPolicy
{
    /// <summary>
    /// Auto-decide policy. One of the <see cref="RenegotiationAutoDecide"/>
    /// constants. Null falls back to <see cref="RenegotiationAutoDecide.Prompt"/>
    /// via <see cref="PolicyLoader.ApplyBuiltInDefaults"/>.
    /// </summary>
    public string? AutoDecide { get; set; }
}

/// <summary>
/// Canonical string constants for <see cref="RenegotiationPolicy.AutoDecide"/>.
/// Mirrors <see cref="RootFallbackAutoDecide"/>'s string-constant pattern
/// so the YAML deserializer accepts the literal token verbatim.
/// </summary>
public static class RenegotiationAutoDecide
{
    /// <summary>Surface the renegotiation human gate (default).</summary>
    public const string Prompt = "prompt";

    /// <summary>Auto-resolve by re-entering the parent's plan-level workflow.
    /// MVP: stub — the apex-driver workflow treats this identically to
    /// <see cref="Prompt"/> until full bubble-up wiring lands.</summary>
    public const string AutoRestart = "auto_restart";

    /// <summary>Auto-resolve by ignoring the child's renegotiation request
    /// and continuing as if the child plan were accepted.</summary>
    public const string Ignore = "ignore";

    /// <summary>True when <paramref name="value"/> is one of the canonical tokens.</summary>
    public static bool IsValid(string value) =>
        value is Prompt or AutoRestart or Ignore;
}

/// <summary>
/// Per-item guidance configuration. Top-level fields act as the workspace
/// default; <see cref="ByType"/> entries override per work-item type
/// (most-specific-wins via <see cref="PolicyResolver.ResolveGuidance"/>).
/// </summary>
public sealed class GuidancePolicy
{
    /// <summary>
    /// Default source for guidance extraction. One of the
    /// <see cref="Polyphony.Sdlc.GuidanceSource"/> constants. Null falls back
    /// to <see cref="Polyphony.Sdlc.GuidanceSource.DescriptionBlock"/> via
    /// <see cref="PolicyLoader.ApplyBuiltInDefaults"/>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// ADO custom field reference name (e.g. <c>Custom.PolyphonyGuidance</c>).
    /// Required when <see cref="Source"/> is
    /// <see cref="Polyphony.Sdlc.GuidanceSource.AdoField"/>; ignored otherwise.
    /// </summary>
    public string? AdoFieldName { get; set; }

    /// <summary>
    /// Optional per-type overrides. Keyed by work-item type name (e.g.
    /// <c>Issue</c>, <c>Task</c>). Each value layers most-specific-wins on
    /// top of the workspace default.
    /// </summary>
    public Dictionary<string, GuidanceRule>? ByType { get; set; }
}

/// <summary>
/// Per-type guidance override. Each field is independently optional — a
/// type-scoped rule that sets only <see cref="Source"/> inherits the
/// workspace default's <see cref="AdoFieldName"/>.
/// </summary>
public sealed class GuidanceRule
{
    /// <summary>One of the <see cref="Polyphony.Sdlc.GuidanceSource"/> constants, or null to inherit.</summary>
    public string? Source { get; set; }

    /// <summary>ADO custom field name (used when the effective source is
    /// <see cref="Polyphony.Sdlc.GuidanceSource.AdoField"/>), or null to inherit.</summary>
    public string? AdoFieldName { get; set; }
}

/// <summary>
/// Per-domain policy with three resolution scopes applied most-specific-wins:
/// <see cref="Root"/> &gt; <see cref="ByType"/> &gt; <see cref="Defaults"/>.
/// </summary>
public sealed class DomainPolicy
{
    /// <summary>Fallback rule for everything that doesn't hit a more specific scope.</summary>
    public ScopeRule? Defaults { get; set; }

    /// <summary>Rule applied when the work item is the root of the workflow run
    /// (the one passed via <c>--input work_item_id</c>).</summary>
    public ScopeRule? Root { get; set; }

    /// <summary>Rules keyed by work-item type name (e.g. <c>Issue</c>, <c>Task</c>).</summary>
    public Dictionary<string, ScopeRule>? ByType { get; set; }
}

/// <summary>
/// A single policy rule. Caps default to null at the rule level; the resolver layers
/// rules together so a more specific scope can override only the fields it cares about
/// while inheriting the rest from <see cref="DomainPolicy.Defaults"/>.
/// </summary>
public sealed class ScopeRule
{
    /// <summary>Mode for this scope. Null inherits from a less-specific scope.</summary>
    public PolicyMode? Mode { get; set; }

    /// <summary>Quality threshold for "clean" reviews. Currently used by approvals only;
    /// PR domain reads but does not yet enforce this field.</summary>
    public QualityThreshold? QualityThreshold { get; set; }

    /// <summary>Approval cap — passed to <c>polyphony plan review --max-cycles</c>.</summary>
    public int? MaxRevisionCycles { get; set; }

    /// <summary>PR cap — number of fixer/reviewer loops before escalating.</summary>
    public int? MaxFixLoops { get; set; }

    /// <summary>Feature-PR cap — outer-loop remediation cycles before escalating.</summary>
    public int? MaxRemediationCycles { get; set; }

    /// <summary>Minimum severity threshold for open-question filtering. Used by open_questions domain.</summary>
    public Severity? MinSeverity { get; set; }

    /// <summary>Maximum question loops before escalating. Used by open_questions domain.</summary>
    public int? MaxQuestionLoops { get; set; }
}

/// <summary>Quality threshold for considering a review "clean" before mode-based routing.</summary>
public sealed class QualityThreshold
{
    public int? AvgScoreAtLeast { get; set; }
    public int? BlockingCountAtMost { get; set; }
}

/// <summary>Concurrency knobs — orthogonal to mode/scope, applied uniformly.</summary>
public sealed class ConcurrencyPolicy
{
    /// <summary>Plan-level for_each cap (max parallel planning sub-workflows).</summary>
    public int? MaxConcurrentChildren { get; set; }
}

