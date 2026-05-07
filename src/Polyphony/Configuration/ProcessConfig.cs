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

    /// <summary>
    /// Top-level facet profile block. Each key is a canonical facet name
    /// (see <see cref="Sdlc.Facet"/>) bound to the skills + MCPs that get
    /// unioned into the agent addendum when an item carries that facet.
    /// Optional — missing or empty means no facet-driven addendum
    /// composition (existing behavior).
    /// </summary>
    /// <remarks>
    /// Composition lives in <see cref="Sdlc.FacetProfileComposer"/>;
    /// load-time validation lives in <see cref="FacetProfileValidator"/>.
    /// PR #1 of Phase 6 ships only the schema + composition + validator;
    /// driver wiring lands in PR #5.
    /// </remarks>
    [YamlMember(Alias = "facets")]
    public Dictionary<string, FacetProfileConfig>? Facets { get; set; }
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

    /// <summary>
    /// Optional. Controls how requirements within an item of this type relate
    /// to one another at edge-graph build time. Allowed values are the
    /// constants on <see cref="Sdlc.ExecutionMode"/>:
    /// <see cref="Sdlc.ExecutionMode.Parallel"/> (default — requirements may
    /// dispatch in parallel once their definitional prerequisites are met) and
    /// <see cref="Sdlc.ExecutionMode.PlanThenImplement"/> (plan must complete
    /// before implementation begins).
    /// </summary>
    /// <remarks>
    /// PR #4 of the Phase 7 edges arc ships only the schema + resolver +
    /// validator for this field. The actual edge injection that gives
    /// <c>plan_then_implement</c> its meaning lands in PR #5. Existing
    /// configs without this key continue to behave exactly as today
    /// (<c>parallel</c> is the default).
    /// </remarks>
    [YamlMember(Alias = "execution_mode")]
    public string? ExecutionMode { get; set; }
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

    /// <summary>
    /// Canonical branch template for merge-group branches. YAML key
    /// <c>mg_branch:</c>. Substitution placeholders: <c>{root_id}</c>,
    /// <c>{slug}</c>, plus the legacy template tokens <c>{n}</c>/<c>{pg}</c>
    /// (the merge-group number) accepted by
    /// <see cref="Routing.BranchNameResolver"/> for back-compat.
    /// </summary>
    /// <remarks>
    /// New code (the Phase 4b verbs in <see cref="Branching.BranchNameBuilder"/>)
    /// builds branch names structurally from the Rev 4 grammar and does not
    /// consult this template. The template is read by the legacy
    /// <c>branch route</c> / <c>branch next-impl</c> code paths via
    /// <see cref="Routing.BranchNameResolver"/>.
    /// </remarks>
    public string MgBranch { get; set; } = "";

    /// <summary>
    /// Deprecated alias for <see cref="MgBranch"/>. Accepted for back-compat with
    /// process configs written before the PG→MergeGroup rename. The loader copies
    /// this onto <see cref="MgBranch"/> when the new key is absent. The validator
    /// emits a deprecation warning (V-17) when only the legacy key is present.
    /// New configs must use <c>mg_branch:</c>.
    /// </summary>
    [YamlMember(Alias = "pg_branch")]
    public string PgBranch { get; set; } = "";

    public string Target { get; set; } = "main";
}

