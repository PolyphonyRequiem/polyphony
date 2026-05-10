namespace Polyphony;

/// <summary>
/// Output of <c>polyphony policy load</c>. Fully resolved policy with built-in
/// defaults applied. Workflow consumers serialize this once at start-of-run and
/// pass it down to per-step <c>policy resolve</c> invocations or read fields directly.
/// </summary>
public sealed record PolicyLoadResult
{
    /// <summary>Schema version of the loaded policy.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Source path the policy was loaded from. <c>null</c> when no file
    /// existed (defaults-only).</summary>
    public string? SourcePath { get; init; }

    /// <summary>True when no policy file was found and the config is fully defaulted.</summary>
    public required bool UsedDefaults { get; init; }

    /// <summary>Resolved approvals defaults (mode + caps + quality threshold).</summary>
    public required PolicyDomainSnapshot Approvals { get; init; }

    /// <summary>Resolved PR defaults (mode + caps).</summary>
    public required PolicyDomainSnapshot Pr { get; init; }

    /// <summary>Resolved open-questions defaults (mode + severity + loops).</summary>
    public required PolicyDomainSnapshot OpenQuestions { get; init; }

    /// <summary>Resolved concurrency caps.</summary>
    public required PolicyConcurrencySnapshot Concurrency { get; init; }

    /// <summary>Resolved per-item guidance source (Phase 6 PR #6).</summary>
    public required PolicyGuidanceSnapshot Guidance { get; init; }

    /// <summary>Resolved root-fallback policy (Phase 1 root-fallback-gate).</summary>
    public required PolicyRootFallbackSnapshot RootFallback { get; init; }

    /// <summary>Resolved renegotiation bubble-up policy (Phase 7 apex-driver).</summary>
    public required PolicyRenegotiationSnapshot Renegotiation { get; init; }
}

/// <summary>
/// Per-domain snapshot exposed via <c>policy load</c>. Captures the defaults plus the
/// raw rules for root and by-type so consumers can introspect the full layering.
/// </summary>
public sealed record PolicyDomainSnapshot
{
    public required string DefaultsMode { get; init; }
    public int? DefaultsMaxRevisionCycles { get; init; }
    public int? DefaultsMaxFixLoops { get; init; }
    public int? DefaultsMaxRemediationCycles { get; init; }
    public string? DefaultsMinSeverity { get; init; }
    public int? DefaultsMaxQuestionLoops { get; init; }
    public int? DefaultsQualityAvgScoreAtLeast { get; init; }
    public int? DefaultsQualityBlockingCountAtMost { get; init; }
    public string? RootMode { get; init; }
    public Dictionary<string, string>? ByTypeMode { get; init; }
}

/// <summary>Concurrency snapshot exposed via <c>policy load</c>.</summary>
public sealed record PolicyConcurrencySnapshot
{
    public required int MaxConcurrentChildren { get; init; }
}

/// <summary>
/// Per-item guidance snapshot exposed via <c>policy load</c>. Captures the
/// effective workspace default plus any per-type overrides so workflows can
/// pass the snapshot down without re-reading the policy file.
/// </summary>
public sealed record PolicyGuidanceSnapshot
{
    /// <summary>One of the <see cref="Polyphony.Sdlc.GuidanceSource"/> constants.</summary>
    public required string Source { get; init; }

    /// <summary>ADO custom field name (only set when <see cref="Source"/> is
    /// <see cref="Polyphony.Sdlc.GuidanceSource.AdoField"/>).</summary>
    public string? AdoFieldName { get; init; }

    /// <summary>Per-type effective source overrides, when configured.</summary>
    public Dictionary<string, string>? ByTypeSource { get; init; }
}

/// <summary>
/// Root-fallback snapshot exposed via <c>policy load</c>. Captures the
/// resolved <c>auto_decide</c> value the <c>root-fallback-gate</c>
/// sub-workflow consumes to decide between the human gate and an
/// auto-resolution path.
/// </summary>
public sealed record PolicyRootFallbackSnapshot
{
    /// <summary>One of <c>prompt</c>, <c>use_active_item</c>, or <c>abort</c>
    /// (the <see cref="Polyphony.Policy.RootFallbackAutoDecide"/> constants).</summary>
    public required string AutoDecide { get; init; }
}

/// <summary>
/// Renegotiation bubble-up snapshot exposed via <c>policy load</c>.
/// Captures the resolved <c>auto_decide</c> value the
/// <c>apex-driver</c> workflow consumes when a child <c>plan-level</c>
/// run returns <c>renegotiation_pending=true</c>.
/// </summary>
public sealed record PolicyRenegotiationSnapshot
{
    /// <summary>One of <c>prompt</c>, <c>auto_restart</c>, or <c>ignore</c>
    /// (the <see cref="Polyphony.Policy.RenegotiationAutoDecide"/> constants).</summary>
    public required string AutoDecide { get; init; }
}
