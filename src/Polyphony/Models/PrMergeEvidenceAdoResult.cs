namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-evidence-ado</c> — the Azure DevOps
/// analogue of <c>polyphony pr merge-evidence-pr</c>. Squash-merges an
/// already-opened evidence PR via
/// <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
/// with <c>deleteSourceBranch=true</c>; evidence branches are single-use.
///
/// <para>Unlike GitHub's <c>gh pr merge --auto</c> (which queues the merge
/// for after policy/check completion), ADO has no auto-merge endpoint —
/// this verb attempts an immediate merge and reports <c>not_mergeable</c>
/// when ADO refuses (e.g. policy not satisfied, conflicts).</para>
///
/// <para><b>Routing-style exit code</b> — always exits 0; consumers branch
/// on <see cref="ErrorCode"/>.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergeEvidenceAdoResult
{
    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>Composite slug — <c>{org}/{project}/{repo}</c>. Empty on early failures.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number being acted on. Echo of <c>--pr-number</c>.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when lookup failed.</summary>
    public required string PrUrl { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>Merge commit SHA when known; empty when not merged or platform did not return one.</summary>
    public required string MergeCommit { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>pr_not_found</c>, <c>stale_head</c>,
    /// <c>not_mergeable</c>, <c>missing_merge_commit</c>, <c>no_pat</c>,
    /// <c>ado_timeout</c>, <c>ado_failed</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
