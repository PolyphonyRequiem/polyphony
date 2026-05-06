namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-plan-pr</c>. Captures the full
/// shape of the create-or-reuse decision plus the front-matter snapshot
/// the verb embedded into the PR body. Workflow consumers route on
/// <see cref="Created"/> + <see cref="Stale"/>:
/// <list type="bullet">
///   <item><c>Created=true</c> — a fresh PR was opened.</item>
///   <item><c>Created=false &amp;&amp; Stale=false &amp;&amp; Error=null</c> — an open PR with a matching snapshot already exists; verb is idempotent and the existing PR can be used.</item>
///   <item><c>Stale=true</c> — an open PR exists but its embedded <c>ancestor_plan_generations</c> snapshot does not match the current manifest. The operator must intervene (close + re-open from current ancestors, or rebase + amend the front-matter). Verb refuses to silently amend.</item>
///   <item><c>Error</c> populated — the verb could not complete; details in <see cref="Error"/>.</item>
/// </list>
/// All snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.
/// </summary>
public sealed record PrOpenPlanPrResult
{
    /// <summary>Run's root (focus) work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>Work-item id this plan PR belongs to.</summary>
    public required int ItemId { get; init; }

    /// <summary>Immediate plan-tree parent's work-item id; 0 when the parent is the root plan or this is the root plan.</summary>
    public required int ParentItemId { get; init; }

    /// <summary>Plan key form used in the manifest (<c>"root"</c> or numeric id as string).</summary>
    public required string ItemKey { get; init; }

    /// <summary>True when this PR is for the root plan (head = <c>plan/{root}</c>).</summary>
    public required bool IsRootPlan { get; init; }

    /// <summary>Source branch of the PR (e.g. <c>plan/100-5678</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>Target branch of the PR (e.g. <c>plan/100</c> or <c>feature/100</c>).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Owner/repo slug the PR belongs to.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number on the platform; 0 when no PR exists yet (verb errored before creation).</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL; empty when verb errored before creation.</summary>
    public required string PrUrl { get; init; }

    /// <summary>Final PR title used (deterministic fallback when <c>--title</c> not set).</summary>
    public required string Title { get; init; }

    /// <summary>True when the verb opened a new PR; false when reusing an existing one.</summary>
    public required bool Created { get; init; }

    /// <summary>True when an existing PR's embedded snapshot did not match the current manifest. When true, <see cref="Created"/> is false and <see cref="Error"/> describes the staleness.</summary>
    public required bool Stale { get; init; }

    /// <summary>The <c>requests_parent_change</c> flag value embedded in the front-matter (false by default).</summary>
    public required bool RequestsParentChange { get; init; }

    /// <summary>The <c>ancestor_plan_generations</c> snapshot embedded in the front-matter — map of ancestor plan key to generation as of branch creation.</summary>
    public required IReadOnlyDictionary<string, int> AncestorPlanGenerations { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
