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
    public string[] Capabilities { get; set; } = [];
    public bool FilingEligible { get; set; }
    public int MaxNestingDepth { get; set; } = 1;
    public string? DecompositionGuidance { get; set; }
    public bool SelfReferential { get; set; }
    public string[] AllowedChildTypes { get; set; } = [];
    public string? Parent { get; set; } // Optional: name of the parent type, if this type is a child of another
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
