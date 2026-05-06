namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-plan-ado</c> — the Azure DevOps analogue
/// of <c>polyphony pr open-plan-pr</c>. Both verbs perform the same logical
/// step (open or reuse a plan PR with the embedded
/// <c>ancestor_plan_generations</c> snapshot in the body's front-matter)
/// but the platform identity differs: ADO PRs live at
/// <c>(organization, project, repository, prNumber)</c>, not
/// <c>(repoSlug, prNumber)</c>.
///
/// <para>Workflow consumers route on
/// <see cref="ErrorCode"/> first (empty ⇒ success or reuse path), then on
/// <see cref="Created"/> + <see cref="Stale"/>:</para>
/// <list type="bullet">
///   <item><c>Created=true, ErrorCode=""</c> — a fresh PR was opened.</item>
///   <item><c>Created=false, Stale=false, ErrorCode=""</c> — an open PR with a matching snapshot already exists; verb is idempotent.</item>
///   <item><c>Created=false, Stale=true, ErrorCode="stale_metadata"</c> — an open PR exists but its embedded snapshot does not match the current manifest. Operator must intervene (close + reopen, or rebase + amend).</item>
///   <item><c>ErrorCode</c> populated with another value — verb refused or could not complete; details in <see cref="Error"/>.</item>
/// </list>
///
/// <para>The verb is <b>routing-style</b>: always exits 0. Consumers
/// branch on <see cref="ErrorCode"/>. This matches
/// <c>polyphony pr vote-ado</c> / <c>polyphony pr poll-status-ado</c>.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrOpenPlanAdoResult
{
    /// <summary>Run's root work-item id, echoed for traceability.</summary>
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

    /// <summary>ADO organization name the PR belongs to (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name the PR belongs to (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier the PR belongs to (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Composite slug — <c>{organization}/{project}/{repository}</c> — surfaced
    /// for cross-platform routing parity with <see cref="PrOpenPlanPrResult.RepoSlug"/>.
    /// Empty when the verb errored before slug construction.
    /// </summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number on ADO; 0 when no PR exists yet (verb errored before creation).</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when verb errored before creation.</summary>
    public required string PrUrl { get; init; }

    /// <summary>Final PR title used (deterministic fallback when <c>--title</c> not set).</summary>
    public required string Title { get; init; }

    /// <summary>True when the verb opened a new PR; false when reusing an existing one.</summary>
    public required bool Created { get; init; }

    /// <summary>True when an existing PR's embedded snapshot did not match the current manifest. When true, <see cref="Created"/> is false and <see cref="ErrorCode"/> is <c>stale_metadata</c>.</summary>
    public required bool Stale { get; init; }

    /// <summary>The <c>requests_parent_change</c> flag value embedded in the front-matter (false by default).</summary>
    public required bool RequestsParentChange { get; init; }

    /// <summary>The <c>ancestor_plan_generations</c> snapshot embedded in the front-matter — map of ancestor plan key to generation as of branch creation.</summary>
    public required IReadOnlyDictionary<string, int> AncestorPlanGenerations { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>manifest_read_failed</c>,
    /// <c>manifest_invalid</c>, <c>stale_metadata</c>, <c>pr_not_found</c>,
    /// <c>ado_timeout</c>, <c>ado_failed</c>, <c>no_pat</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
