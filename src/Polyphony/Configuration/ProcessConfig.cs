using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

public sealed class ProcessConfig
{
    public int SchemaVersion { get; set; }
    public string ProcessTemplate { get; set; } = "";
    public Dictionary<string, TypeConfig> Types { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Transitions { get; set; } = new();
    public ReviewPolicies? ReviewPolicies { get; set; }
    public BranchStrategy? BranchStrategy { get; set; }
    public string Platform { get; set; } = "github";
}

public sealed class TypeConfig
{
    /// <summary>
    /// Kinds of work this type carries. Recognized values: <c>plannable</c>,
    /// <c>actionable</c>, <c>implementable</c>. The legacy YAML key
    /// <c>capabilities:</c> is still accepted as an alias during the migration
    /// window — see docs/glossary.md (Phase 1 of the PR-lifecycle overhaul).
    /// </summary>
    [YamlMember(Alias = "facets")]
    public string[] Facets { get; set; } = [];

    /// <summary>
    /// Deprecated alias for <see cref="Facets"/>. Accepted for back-compat with
    /// process configs written before the capability→facet rename. New configs
    /// must use <c>facets:</c>. The loader copies this onto <see cref="Facets"/>
    /// when the new key is absent.
    /// </summary>
    [YamlMember(Alias = "capabilities")]
    public string[]? CapabilitiesLegacy { get; set; }

    public bool FilingEligible { get; set; }
    public int MaxNestingDepth { get; set; } = 1;
    public string? DecompositionGuidance { get; set; }
    public bool SelfReferential { get; set; }
    public string[] AllowedChildTypes { get; set; } = [];
    public string? Parent { get; set; }

    /// <summary>
    /// Optional. When supplied, the planner has explicitly declared that items of
    /// this type WILL have children (i.e. the deriver should emit
    /// <c>children_seeded</c>). When <c>null</c>, the consumer falls back to a
    /// best-effort inference from observable signals (existing children,
    /// <see cref="DecompositionGuidance"/>, <see cref="AllowedChildTypes"/>).
    /// </summary>
    /// <remarks>
    /// Per the glossary, "decomposable" historically meant <i>permission</i> to
    /// have children. Here, the field is the planner's <i>directive</i> — the
    /// downstream Phase 7 worklist work will eventually let planners declare this
    /// per-instance rather than per-type.
    /// </remarks>
    [YamlMember(Alias = "decomposable")]
    public bool? Decomposable { get; set; }

    /// <summary>
    /// Optional. Required by the deriver only when both <c>actionable</c> and
    /// <c>implementable</c> facets are present on this type (no current type
    /// has actionable, so this is forward-looking).
    /// </summary>
    [YamlMember(Alias = "facet_order")]
    public string[]? FacetOrder { get; set; }

    /// <summary>
    /// Optional. Required by the deriver only when <c>actionable</c> is in
    /// <see cref="Facets"/>. Allowed values: <c>polyphony</c>, <c>human</c>.
    /// </summary>
    [YamlMember(Alias = "actionable_executor")]
    public string? ActionableExecutor { get; set; }
}

public sealed class ReviewPolicies
{
    public Dictionary<string, ReviewPolicy>? Planning { get; set; }
    public Dictionary<string, ReviewPolicy>? Implementation { get; set; }
    public Dictionary<string, ReviewPolicy>? Remediation { get; set; }
}

public sealed class ReviewPolicy
{
    public bool AgentReview { get; set; }
    public bool HumanReview { get; set; }
    public bool AutoMerge { get; set; }
}

public sealed class BranchStrategy
{
    public string FeatureBranch { get; set; } = "";
    public string PlanningBranch { get; set; } = "";
    public string PgBranch { get; set; } = "";
    public string Target { get; set; } = "main";
}

