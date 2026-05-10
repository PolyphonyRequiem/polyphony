namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr open-mg-ado</c> — the Azure DevOps analogue of
/// <c>polyphony pr open-mg-pr</c>. Both verbs perform the same logical step
/// (open or reuse the PR promoting an <c>mg/{root}_{mg_path}</c> branch into
/// its parent merge-group branch when nested, or the feature branch when
/// top-level). The platform identity differs: ADO PRs live at
/// <c>(organization, project, repository, prNumber)</c>, not
/// <c>(repoSlug, prNumber)</c>.
///
/// <para><b>Routing-style exit code</b> — always exits 0; consumers branch
/// on <see cref="ErrorCode"/>. This matches the other ADO-side verbs
/// (<c>vote-ado</c>, <c>poll-status-ado</c>, <c>open-plan-ado</c>,
/// <c>merge-plan-ado</c>) and contrasts with the GitHub-side
/// <c>open-mg-pr</c> which uses categorical exit codes.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrOpenMergeGroupAdoResult
{
    /// <summary>The root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path.</summary>
    public required string MgPath { get; init; }

    /// <summary>The fully-qualified head branch (e.g. <c>mg/100_core</c>).</summary>
    public required string HeadBranch { get; init; }

    /// <summary>
    /// The fully-qualified base branch — the parent merge-group branch when
    /// nested, or the feature branch when top-level.
    /// </summary>
    public required string BaseBranch { get; init; }

    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Composite slug — <c>{organization}/{project}/{repository}</c> — surfaced
    /// for cross-platform routing parity with
    /// <see cref="PrOpenMergeGroupResult"/> consumers (which echo a
    /// GitHub-style <c>owner/repo</c>). Empty when the verb errored before
    /// slug construction.
    /// </summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number assigned by ADO. Zero when no PR exists yet (verb errored before creation).</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when verb errored before creation.</summary>
    public required string PrUrl { get; init; }

    /// <summary>Final PR title used (deterministic fallback when <c>--title</c> not set).</summary>
    public required string Title { get; init; }

    /// <summary>True when the verb opened a new PR; false when reusing an existing one.</summary>
    public required bool Created { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>missing_head_branch</c>,
    /// <c>missing_base_branch</c>, <c>pr_not_found</c>, <c>ado_timeout</c>,
    /// <c>ado_failed</c>, <c>no_pat</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
