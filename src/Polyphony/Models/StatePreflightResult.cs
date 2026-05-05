namespace Polyphony;

/// <summary>
/// One row in a preflight check report — the JSON contract emitted by
/// <c>polyphony state preflight</c> and <c>polyphony state preflight-lite</c>.
/// Mirrors the shape produced by <c>scripts/preflight-check.ps1</c>'s
/// <c>New-CheckResult</c> helper.
/// </summary>
public sealed record PreflightCheck
{
    /// <summary>Stable name (e.g. <c>git_repo</c>, <c>twig_cli</c>).</summary>
    public required string Name { get; init; }

    /// <summary>True when the check passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>Human-readable detail line (success or failure reason).</summary>
    public required string Detail { get; init; }

    /// <summary>Optional remediation hint shown when <see cref="Passed"/> is false.</summary>
    public string? Remediation { get; init; }
}

/// <summary>
/// Output of <c>polyphony state preflight-lite</c>: a single flat list of
/// 3 quick prerequisite checks for the planning sub-workflow.
/// </summary>
public sealed record StatePreflightLiteResult
{
    /// <summary>True when no required checks failed.</summary>
    public required bool Ready { get; init; }

    /// <summary>One-line summary suitable for surfacing in a gate prompt.</summary>
    public required string Summary { get; init; }

    /// <summary>Ordered list of checks performed (git_repo, twig_cli, polyphony_cli).</summary>
    public required IReadOnlyList<PreflightCheck> Checks { get; init; }

    /// <summary>Number of failed required checks.</summary>
    public required int FailedCount { get; init; }
}

/// <summary>
/// Optional metadata block included in a full preflight result.
/// </summary>
public sealed record PreflightDetails
{
    public required int WorkItemId { get; init; }
    public string? AdoOrg { get; init; }
    public string? AdoProject { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Output of <c>polyphony state preflight</c>: the root-workflow gate
/// payload. Splits checks into required (gating) and advisory (warning).
/// </summary>
public sealed record StatePreflightResult
{
    /// <summary>True when no required checks failed.</summary>
    public required bool Ready { get; init; }

    /// <summary>One-line summary suitable for surfacing in a gate prompt.</summary>
    public required string Summary { get; init; }

    /// <summary>Required checks — failure here gates the workflow.</summary>
    public required IReadOnlyList<PreflightCheck> RequiredChecks { get; init; }

    /// <summary>Advisory checks — failure surfaces warnings but doesn't gate.</summary>
    public required IReadOnlyList<PreflightCheck> AdvisoryChecks { get; init; }

    /// <summary>Number of failed required checks.</summary>
    public required int FailedCount { get; init; }

    /// <summary>Number of failed advisory checks.</summary>
    public required int WarningCount { get; init; }

    /// <summary>Diagnostic metadata (work item ID, ADO org/project).</summary>
    public required PreflightDetails Details { get; init; }
}

