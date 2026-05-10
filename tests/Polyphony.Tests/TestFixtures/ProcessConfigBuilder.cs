using Polyphony.Configuration;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Fluent builder for <see cref="ProcessConfig"/> instances in Polyphony tests.
/// Starts with a minimal "Basic" template — add only the types and strategies each test needs.
/// </summary>
/// <remarks>
/// State→category mappings for tests are auto-synthesized from <see cref="WellKnownStateCategories"/>
/// covering the standard ADO process templates (Basic / Agile / Scrum / CMMI). Tests using custom
/// state vocabulary (e.g. CloudVault's Started/Cut) should call <see cref="WithStates"/> to declare
/// them explicitly. Production configs MUST declare states explicitly — V-21 enforces.
/// </remarks>
public sealed class ProcessConfigBuilder
{
    /// <summary>
    /// Heuristic state-name → snake_case category map for test scaffolding only.
    /// This catalog deliberately mirrors the union of state names ADO ships across
    /// the four standard process templates so existing tests need no per-fixture
    /// state declarations. Production code never consults this table — it reads
    /// <see cref="ProcessConfig.States"/> directly (issue #281).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WellKnownStateCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["new"] = "proposed",
            ["to do"] = "proposed",
            ["proposed"] = "proposed",
            ["active"] = "in_progress",
            ["doing"] = "in_progress",
            ["committed"] = "in_progress",
            ["in progress"] = "in_progress",
            ["approved"] = "in_progress",
            ["resolved"] = "resolved",
            ["closed"] = "completed",
            ["done"] = "completed",
            ["completed"] = "completed",
            ["removed"] = "removed",
        };

    private string _processTemplate = "Basic";
    private readonly Dictionary<string, TypeConfig> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _explicitStates = new(StringComparer.OrdinalIgnoreCase);
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
    /// <c>merge_group_branch</c>). Production tests should set
    /// <paramref name="MergeGroupBranch"/>.
    /// </remarks>
    public ProcessConfigBuilder WithBranchStrategy(
        string featureBranch = "feature/{id}",
        string planningBranch = "planning/{id}",
        string MergeGroupBranch = "feature/{id}-mg-{n}",
        string target = "main")
    {
        _branchStrategy = new BranchStrategy
        {
            FeatureBranch = featureBranch,
            PlanningBranch = planningBranch,
            MergeGroupBranch = MergeGroupBranch,
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

    /// <summary>
    /// Explicitly declares a per-type state→category map, mirroring the
    /// production <c>states:</c> YAML block. Use this for tests with custom
    /// state vocabulary that <see cref="WellKnownStateCategories"/> doesn't
    /// cover (e.g. CMMI literal "Started", "Cut"). Categories must be one of
    /// <c>proposed | in_progress | resolved | completed | removed</c>.
    /// </summary>
    public ProcessConfigBuilder WithStates(string typeName, Dictionary<string, string> states)
    {
        _explicitStates[typeName] = new Dictionary<string, string>(states, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public ProcessConfig Build()
    {
        var transitions = new Dictionary<string, Dictionary<string, string>>(_transitions, StringComparer.OrdinalIgnoreCase);
        var states = SynthesizeStates(transitions);

        return new ProcessConfig
        {
            ProcessTemplate = _processTemplate,
            Types = new Dictionary<string, TypeConfig>(_types, StringComparer.OrdinalIgnoreCase),
            Transitions = transitions,
            States = states,
            BranchStrategy = _branchStrategy,
            Platform = _platform,
            Facets = _facetProfiles is null
                ? null
                : new Dictionary<string, FacetProfileConfig>(_facetProfiles, StringComparer.Ordinal),
        };
    }

    private Dictionary<string, Dictionary<string, string>> SynthesizeStates(
        Dictionary<string, Dictionary<string, string>> transitions)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var typeName in _types.Keys)
        {
            // Explicit declarations win — let CMMI/CloudVault tests bring their own vocabulary.
            if (_explicitStates.TryGetValue(typeName, out var explicitMap))
            {
                result[typeName] = new Dictionary<string, string>(explicitMap, StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var stateMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Seed from the well-known catalog so the standard state names are always categorizable.
            foreach (var (name, category) in WellKnownStateCategories)
                stateMap[name] = category;

            // Ensure every transition target is also declared, even if it's not in the catalog.
            // Tests using custom state names without WithStates(...) get a "completed" default
            // (matches the historical FallbackCategory behavior for unknown names — Unknown
            // would surface as a routing failure that the original heuristic wouldn't have hit).
            if (transitions.TryGetValue(typeName, out var typeTransitions))
            {
                foreach (var targetState in typeTransitions.Values)
                {
                    if (!stateMap.ContainsKey(targetState))
                    {
                        stateMap[targetState] = WellKnownStateCategories.TryGetValue(targetState, out var cat)
                            ? cat
                            : "completed";
                    }
                }
            }

            result[typeName] = stateMap;
        }

        return result;
    }
}

