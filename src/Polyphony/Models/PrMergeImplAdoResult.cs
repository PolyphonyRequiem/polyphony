namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-impl-ado</c> — the Azure DevOps analogue
/// of <c>polyphony pr merge-impl-pr</c>. Merges an impl PR (head
/// <c>impl/{root}-{item}</c>) into its enclosing merge-group branch (base
/// <c>mg/{root}_{mg_path}</c>) via
/// <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
/// using the <c>squash</c> merge strategy. The head branch IS deleted after
/// merge — impl branches are single-use and never reused.
///
/// <para><b>Routing-style exit code</b> — always exits 0; consumers branch
/// on <see cref="ErrorCode"/>.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergeImplAdoResult
{
    /// <summary>The root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>The non-root (task / impl) work-item id.</summary>
    public required int ItemId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path of the enclosing MG.</summary>
    public required string MgPath { get; init; }

    /// <summary>The fully-qualified head branch (e.g. <c>impl/100-200</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The fully-qualified base branch (the enclosing merge-group branch).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>Composite slug — <c>{org}/{project}/{repo}</c>. Empty on early failures.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number being acted on. Zero when no PR was found.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when no PR was found.</summary>
    public required string PrUrl { get; init; }

    /// <summary>PR state observed at poll time (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>); empty if lookup failed.</summary>
    public required string PrState { get; init; }

    /// <summary>Always the literal <c>"squash"</c> — included for workflow log clarity.</summary>
    public required string Method { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>True when the head branch was requested to be deleted (always true for impl PRs).</summary>
    public required bool DeleteBranch { get; init; }

    /// <summary>Merge commit SHA when known; empty when not merged or platform did not return one.</summary>
    public required string MergeCommit { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>pr_not_found</c>, <c>pr_state_invalid</c>,
    /// <c>stale_head</c>, <c>missing_merge_commit</c>,
    /// <c>ado_complete_failed</c>, <c>no_pat</c>, <c>ado_timeout</c>,
    /// <c>ado_failed</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
