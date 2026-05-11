namespace Polyphony;

/// <summary>
/// Result envelope for <c>polyphony worktree create --apex N --branch B [--ref R]</c>.
///
/// <para>This verb is routing-style: it always exits 0; consumers branch on
/// <see cref="Outcome"/> and (when failed) <see cref="Reason"/>.</para>
///
/// <para>Outcomes:</para>
/// <list type="bullet">
///   <item><c>created</c> — new worktree + new local branch from <c>--ref</c>.</item>
///   <item><c>attached</c> — new worktree pointing at an existing local branch.</item>
///   <item><c>idempotent</c> — target path is already a worktree on the expected branch.</item>
///   <item><c>failed</c> — see <see cref="Reason"/>.</item>
/// </list>
///
/// <para>Reasons (only set when <see cref="Outcome"/> is <c>failed</c>):</para>
/// <list type="bullet">
///   <item><c>invalid_apex</c> — <c>--apex</c> is not a positive int.</item>
///   <item><c>invalid_branch</c> — <c>--branch</c> failed <see cref="Polyphony.Infrastructure.Worktrees.BranchSlug.TryParse"/>.</item>
///   <item><c>branch_apex_mismatch</c> — parsed root id of <c>--branch</c> does not equal <c>--apex</c>.</item>
///   <item><c>unsupported_branch_kind</c> — <c>--branch</c> is a <c>feature/{N}</c> branch; use <c>worktree init-apex</c>.</item>
///   <item><c>common_dir_unavailable</c> — <c>git rev-parse --git-common-dir</c> returned no usable path.</item>
///   <item><c>filesystem_failure</c> — derived worktree path violates the runs-root or main-worktree boundary invariant.</item>
///   <item><c>apex_not_initialized</c> — <c>{apex_root}/feature-{N}</c> is not a registered worktree on <c>feature/{N}</c>; run <c>worktree init-apex --apex N</c> first.</item>
///   <item><c>path_exists_wrong_branch</c> — target worktree path is on a different branch.</item>
///   <item><c>path_exists_not_worktree</c> — target path exists (file or directory) but is not a registered worktree.</item>
///   <item><c>branch_in_use</c> — local branch exists and is checked out at another worktree.</item>
///   <item><c>remote_branch_exists</c> — local branch missing but <c>origin/{branch}</c> exists; fetch and create the local branch first.</item>
///   <item><c>ref_required</c> — local branch missing and <c>--ref</c> not supplied (no HEAD default).</item>
///   <item><c>git_failure</c> — any git invocation failed (or <c>git worktree list --porcelain</c> output was malformed).</item>
/// </list>
/// </summary>
public sealed record WorktreeCreateResult
{
    /// <summary>Echo of the <c>--apex</c> input.</summary>
    public required int ApexId { get; init; }

    /// <summary>Outcome of the operation. See record-level docs for valid values.</summary>
    public required string Outcome { get; init; }

    /// <summary>The (validated) branch name. Null if <c>--branch</c> failed parse.</summary>
    public string? Branch { get; init; }

    /// <summary>The filesystem-safe slug derived from <see cref="Branch"/>. Null if <c>--branch</c> failed parse.</summary>
    public string? Slug { get; init; }

    /// <summary>Echo of the <c>--ref</c> input. Null if not supplied.</summary>
    public string? Ref { get; init; }

    /// <summary>Absolute path to the per-apex container directory. Null when path resolution failed.</summary>
    public string? ApexRoot { get; init; }

    /// <summary>Absolute path to the per-item worktree. Null when path resolution failed.</summary>
    public string? WorktreePath { get; init; }

    /// <summary>Failure category. Null when <see cref="Outcome"/> is not <c>failed</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Operator-readable explanation. Null when no extra detail is available.</summary>
    public string? Error { get; init; }
}
