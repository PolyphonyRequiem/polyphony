namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-evidence-pr</c> — the unified, platform-aware
/// verb that squash-merges an evidence PR. Replaces the bare <c>gh pr merge</c>
/// pwsh shell-out previously inlined in <c>actionable.yaml</c>.
///
/// <para>Behavior by platform:</para>
/// <list type="bullet">
///   <item><b>GitHub</b>: invokes <c>gh pr merge --squash --auto --delete-branch</c>.
///     <c>--auto</c> queues the merge for after policy/check completion, so
///     <see cref="Merged"/>=true reports "queued successfully" rather than
///     "merge commit produced". <see cref="MergeCommit"/> is empty for the
///     same reason.</item>
///   <item><b>ADO</b>: invokes
///     <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
///     with squash strategy + delete-branch=true. ADO has no auto-merge
///     equivalent — the merge happens immediately or fails. On success
///     <see cref="MergeCommit"/> carries the merge commit SHA.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergeEvidenceResult
{
    /// <summary>PR number being acted on. Echo of <c>--pr-number</c>.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical platform page); empty when not provided / not resolvable.</summary>
    public required string PrUrl { get; init; }

    /// <summary>
    /// True when the merge completed (or, for GitHub <c>--auto</c>, was
    /// queued successfully). Workflow consumers branch on this to decide
    /// terminal-vs-error routing.
    /// </summary>
    public required bool Merged { get; init; }

    /// <summary>
    /// True when ADO reported the PR was already in completed state before
    /// this verb ran. Always false on GitHub (gh does not surface this
    /// distinction synchronously when --auto is supplied).
    /// </summary>
    public bool AlreadyMerged { get; init; }

    /// <summary>
    /// Merge commit SHA when known. Populated on ADO success; empty on
    /// GitHub (the auto-merge queue resolves asynchronously).
    /// </summary>
    public string MergeCommit { get; init; } = "";

    /// <summary>
    /// ADO organization name. Empty on the GitHub branch.
    /// </summary>
    public string Organization { get; init; } = "";

    /// <summary>
    /// ADO project name. Empty on the GitHub branch.
    /// </summary>
    public string Project { get; init; } = "";

    /// <summary>
    /// Repository identifier. ADO repo name/GUID on the ADO branch;
    /// <c>owner/name</c> slug on the GitHub branch (when resolvable).
    /// </summary>
    public string Repository { get; init; } = "";

    /// <summary>
    /// Composite slug. ADO: <c>{org}/{project}/{repo}</c>. GitHub:
    /// <c>{owner}/{name}</c>. Empty when not resolvable.
    /// </summary>
    public string RepoSlug { get; init; } = "";

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
