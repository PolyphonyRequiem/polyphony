using Polyphony.Configuration;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Fluent builder for <see cref="ProcessConfig"/> instances in Polyphony tests.
/// Starts with a minimal "Basic" template — add only the types and strategies each test needs.
/// </summary>
public sealed class ProcessConfigBuilder
{
    private string _processTemplate = "Basic";
    private readonly Dictionary<string, TypeConfig> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private BranchStrategy? _branchStrategy;
    private string _platform = "github";
    private Dictionary<string, FacetProfileConfig>? _facetProfiles;

    public ProcessConfigBuilder WithProcessTemplate(string template) { _processTemplate = template; return this; }
    public ProcessConfigBuilder WithPlatform(string platform) { _platform = platform; return this; }

    /// <summary>
    /// Adds a work item type with its facets and per-event transitions.
    /// </summary>
    public ProcessConfigBuilder WithType(
        string name,
        string[] facets,
        Dictionary<string, string>? transitions = null,
        bool selfReferential = false,
        string[]? allowedChildTypes = null,
        string? parent = null)
    {
        _types[name] = new TypeConfig {
            Facets = facets,
            SelfReferential = selfReferential,
            AllowedChildTypes = allowedChildTypes ?? System.Array.Empty<string>(),
            Parent = parent
        };

        if (transitions is not null)
            _transitions[name] = new Dictionary<string, string>(transitions, StringComparer.OrdinalIgnoreCase);

        return this;
    }

    /// <summary>
    /// Adds a work item type with a parent relationship (legacy, prefer WithType overload).
    /// </summary>
    public ProcessConfigBuilder WithTypeWithParent(
        string name,
        string[] facets,
        string parent,
        Dictionary<string, string>? transitions = null,
        bool selfReferential = false,
        string[]? allowedChildTypes = null)
    {
        _types[name] = new TypeConfig {
            Facets = facets,
            Parent = parent,
            SelfReferential = selfReferential,
            AllowedChildTypes = allowedChildTypes ?? System.Array.Empty<string>()
        };

        if (transitions is not null)
            _transitions[name] = new Dictionary<string, string>(transitions, StringComparer.OrdinalIgnoreCase);

        return this;
    }

    /// <summary>Configures the branch naming strategy.</summary>
    /// <remarks>
    /// <paramref name="MergeGroupBranch"/> is the canonical Phase-4 template (YAML key
    /// <c>mg_branch</c>). <paramref name="pgBranchLegacy"/> populates the
    /// deprecated <c>pg_branch</c> field directly without populating
    /// <see cref="BranchStrategy.MergeGroupBranch"/> — used to exercise V-17 and the
    /// loader's legacy-key fallback. Production tests should set only
    /// <paramref name="MergeGroupBranch"/>.
    /// </remarks>
    public ProcessConfigBuilder WithBranchStrategy(
        string featureBranch = "feature/{id}",
        string planningBranch = "planning/{id}",
        string MergeGroupBranch = "feature/{id}-mg-{n}",
        string target = "main",
        string? pgBranchLegacy = null)
    {
        _branchStrategy = new BranchStrategy
        {
            FeatureBranch = featureBranch,
            PlanningBranch = planningBranch,
            MergeGroupBranch = MergeGroupBranch,
            PgBranch = pgBranchLegacy ?? "",
            Target = target,
        };
        return this;
    }

    /// <summary>
    /// Adds a facet profile (top-level <c>facets:</c> block in YAML) that the
    /// driver will union into the agent addendum when an item carries
    /// <paramref name="facetName"/>. Calling this multiple times for the
    /// same <paramref name="facetName"/> overwrites the prior entry — the
    /// load-time validator (V-20) governs duplicate detection within a
    /// single profile, not across builder calls.
    /// </summary>
    public ProcessConfigBuilder WithFacetProfile(
        string facetName,
        string[]? skills = null,
        string[]? mcps = null)
    {
        _facetProfiles ??= new Dictionary<string, FacetProfileConfig>(StringComparer.Ordinal);
        _facetProfiles[facetName] = new FacetProfileConfig
        {
            Skills = skills ?? System.Array.Empty<string>(),
            Mcps = mcps ?? System.Array.Empty<string>(),
        };
        return this;
    }

    public ProcessConfig Build()
    {
        return new ProcessConfig
        {
            ProcessTemplate = _processTemplate,
            Types = new Dictionary<string, TypeConfig>(_types, StringComparer.OrdinalIgnoreCase),
            Transitions = new Dictionary<string, Dictionary<string, string>>(_transitions, StringComparer.OrdinalIgnoreCase),
            BranchStrategy = _branchStrategy,
            Platform = _platform,
            Facets = _facetProfiles is null
                ? null
                : new Dictionary<string, FacetProfileConfig>(_facetProfiles, StringComparer.Ordinal),
        };
    }
}

