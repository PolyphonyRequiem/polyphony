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

    public ProcessConfigBuilder WithProcessTemplate(string template) { _processTemplate = template; return this; }
    public ProcessConfigBuilder WithPlatform(string platform) { _platform = platform; return this; }

    /// <summary>
    /// Adds a work item type with its capabilities and per-event transitions.
    /// </summary>
    public ProcessConfigBuilder WithType(
        string name,
        string[] capabilities,
        Dictionary<string, string>? transitions = null)
    {
        _types[name] = new TypeConfig { Capabilities = capabilities };

        if (transitions is not null)
            _transitions[name] = new Dictionary<string, string>(transitions, StringComparer.OrdinalIgnoreCase);

        return this;
    }

    /// <summary>
    /// Adds a work item type with a parent relationship.
    /// </summary>
    public ProcessConfigBuilder WithTypeWithParent(
        string name,
        string[] capabilities,
        string parent,
        Dictionary<string, string>? transitions = null)
    {
        _types[name] = new TypeConfig { Capabilities = capabilities, Parent = parent };

        if (transitions is not null)
            _transitions[name] = new Dictionary<string, string>(transitions, StringComparer.OrdinalIgnoreCase);

        return this;
    }

    /// <summary>Configures the branch naming strategy.</summary>
    public ProcessConfigBuilder WithBranchStrategy(
        string featureBranch = "feature/{id}",
        string planningBranch = "planning/{id}",
        string pgBranch = "feature/{id}-pg-{pg}",
        string target = "main")
    {
        _branchStrategy = new BranchStrategy
        {
            FeatureBranch = featureBranch,
            PlanningBranch = planningBranch,
            PgBranch = pgBranch,
            Target = target,
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
        };
    }
}
