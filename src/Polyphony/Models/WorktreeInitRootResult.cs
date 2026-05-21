namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree init-apex --apex N</c>. Reports the
/// resolved apex root (the per-apex container under the runs-root) and
/// the <c>feature/{N}</c> worktree that was created/attached/idempotently
/// confirmed.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch
/// on <see cref="Outcome"/> and <see cref="Reason"/>. <c>Outcome</c> is
/// one of:</para>
///
/// <list type="bullet">
///   <item><c>created</c>     — branch did not exist, worktree created via <c>git worktree add -b</c> rooted at local <c>main</c>.</item>
///   <item><c>attached</c>    — branch existed locally, worktree attached via <c>git worktree add &lt;path&gt; &lt;branch&gt;</c>.</item>
///   <item><c>idempotent</c>  — target path already a worktree on the expected branch (or became so under a race; see post-failure re-list).</item>
///   <item><c>dry_run</c>     — <c>--dry-run</c> was set; paths were resolved but no filesystem or git mutations performed. <see cref="DryRun"/> is true.</item>
///   <item><c>failed</c>      — refusal or git failure; <see cref="Reason"/> classifies, <see cref="Error"/> carries detail.</item>
/// </list>
///
/// <para><see cref="Reason"/> is null on success and one of these on
/// failure:</para>
/// <list type="bullet">
///   <item><c>invalid_apex</c>             — <c>--apex</c> was supplied but non-positive.</item>
///   <item><c>common_dir_unavailable</c>   — <c>git rev-parse --git-common-dir</c> returned no usable path.</item>
///   <item><c>filesystem_failure</c>       — could not create the apex-root container directory.</item>
///   <item><c>git_failure</c>              — a git invocation failed; <see cref="Error"/> carries stderr.</item>
///   <item><c>path_exists_wrong_branch</c> — target path is a worktree, but on a different branch.</item>
///   <item><c>path_exists_not_worktree</c> — target path exists on disk but is not a registered worktree.</item>
///   <item><c>branch_in_use</c>            — <c>feature/{N}</c> exists locally and is already checked out elsewhere.</item>
///   <item><c>remote_branch_exists</c>     — <c>feature/{N}</c> does not exist locally but does on origin; init-apex is local-only and refuses to fork.</item>
/// </list>
///
/// <para>When the failure is detected before path resolution
/// (<c>invalid_apex</c>, <c>common_dir_unavailable</c>),
/// <see cref="ApexRoot"/>, <see cref="WorktreePath"/>, and
/// <see cref="Branch"/> may be null.</para>
/// </summary>
public sealed record WorktreeInitApexResult
{
    /// <summary>The apex id that was passed to <c>--apex</c> (echoed back for confirmation).</summary>
    public required int ApexId { get; init; }

    /// <summary>
    /// Absolute path to <c>{runs_root}/apex-{N}/</c> — the per-apex
    /// container that holds all per-item worktrees for this apex.
    /// Null when failure occurred before path resolution.
    /// </summary>
    public string? ApexRoot { get; init; }

    /// <summary>
    /// Absolute path to the per-run worktree root (<c>{parent}/{repo}-runs/</c>)
    /// resolved from <c>git rev-parse --git-common-dir</c>. Null when failure
    /// occurred before path resolution. Surfaced for launcher consumption so
    /// PowerShell does not have to mirror <see cref="Polyphony.Infrastructure.Worktrees.RunsRootResolver"/>.
    /// </summary>
    public string? RunsRoot { get; init; }

    /// <summary>
    /// Absolute path to the conventional main worktree (sibling of the bare
    /// gitdir, named <c>{parent}/{repo}/</c>). Null when failure occurred
    /// before path resolution. Surfaced for launcher consumption so the
    /// hijack-refusal check (<c>WorktreeRoot</c> is or is inside main)
    /// can be done without mirroring resolver logic in PowerShell.
    /// </summary>
    public string? MainWorktreePath { get; init; }

    /// <summary>
    /// Absolute path to the <c>feature/{N}</c> worktree at
    /// <c>{runs_root}/apex-{N}/feature-{N}/</c>. Null when failure
    /// occurred before path resolution.
    /// </summary>
    public string? WorktreePath { get; init; }

    /// <summary>
    /// The fully-qualified branch name (<c>feature/{N}</c>). Null when
    /// failure occurred before path resolution.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// One of <c>created</c>, <c>attached</c>, <c>idempotent</c>,
    /// <c>dry_run</c>, <c>failed</c>. See record-level docs for semantics.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// True when <c>--dry-run</c> was supplied: paths are resolved and
    /// the create-or-attach matrix is classified, but no filesystem or
    /// git mutations are performed. The launcher uses this to derive
    /// paths for <c>-DryRun</c> mode without creating worktrees.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Failure-classification code; null on success. See record-level
    /// docs for the enumeration.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Human-readable diagnostic (typically git stderr or a filesystem
    /// exception message); null on success.
    /// </summary>
    public string? Error { get; init; }
}
