namespace Polyphony.Policy;

/// <summary>
/// Root-level policy configuration loaded from <c>.conductor/policy.yaml</c>.
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

    /// <summary>Concurrency knobs (orthogonal to mode/scope).</summary>
    public ConcurrencyPolicy? Concurrency { get; set; }
}

/// <summary>
/// Per-domain policy with three resolution scopes applied most-specific-wins:
/// <see cref="Root"/> &gt; <see cref="ByType"/> &gt; <see cref="Defaults"/>.
/// </summary>
public sealed class DomainPolicy
{
    /// <summary>Fallback rule for everything that doesn't hit a more specific scope.</summary>
    public ScopeRule? Defaults { get; set; }

    /// <summary>Rule applied when the work item is the apex of the workflow run
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

    /// <summary>Implementation for_each cap (max parallel PG implementation sub-workflows).</summary>
    public int? MaxConcurrentPgs { get; set; }
}
