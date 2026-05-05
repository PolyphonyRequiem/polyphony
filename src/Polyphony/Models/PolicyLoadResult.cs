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
    public required int MaxConcurrentPgs { get; init; }
}
