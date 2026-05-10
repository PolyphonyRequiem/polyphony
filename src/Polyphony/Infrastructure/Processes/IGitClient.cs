namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Typed wrapper over the <c>git</c> CLI. Each method maps to a single
/// <c>git</c> invocation. Returns null / empty list on benign failures
/// (e.g. "not a git repo", "no remotes"). Throws
/// <see cref="ExternalToolException"/> on unexpected non-zero exits.
/// </summary>
public interface IGitClient
{
    /// <summary>
    /// <c>git rev-parse --show-toplevel</c>. Returns the absolute path to
    /// the repository root, or null when not inside a git repo.
    /// </summary>
    Task<string?> GetTopLevelAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>git rev-parse --path-format=absolute --git-common-dir</c>. Returns
    /// the absolute path to the **shared** git directory for the current
    /// worktree. In the main worktree this is the same as <c>.git</c>; in a
    /// linked worktree (created via <c>git worktree add</c>) it is the main
    /// worktree's <c>.git</c>, NOT the per-worktree <c>.git/worktrees/{name}</c>
    /// directory. Returns null when not inside a git repo.
    ///
    /// <para><b>The <c>--path-format=absolute</c> qualifier is mandatory.</b>
    /// The default output is relative to the current working directory
    /// (<c>.git</c> from the main worktree, <c>../.git/worktrees/{name}</c>
    /// or similar from a linked worktree). Path resolvers that consume the
    /// relative form silently produce inconsistent absolute paths between
    /// worktrees, defeating the only reason callers ask for the common dir.
    /// See <c>docs/decisions/branch-model.md § State location (Rev 4.2
    /// amendment)</c>.</para>
    ///
    /// <para><b><c>--git-common-dir</c>, not <c>--git-dir</c>.</b>
    /// <c>--git-dir</c> resolves to the per-worktree
    /// <c>.git/worktrees/{name}</c> directory in linked worktrees and would
    /// re-introduce the divergence we are trying to eliminate.</para>
    /// </summary>
    Task<string?> GetCommonDirAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>git branch --show-current</c>. Returns the current branch name,
    /// or null when detached / not in a repo.
    /// </summary>
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>git remote get-url {remote}</c>. Returns the configured URL for
    /// the named remote, or null when the remote is missing.
    /// </summary>
    Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);

    /// <summary>
    /// <c>git branch -r</c>. Returns the list of remote-tracking branch
    /// names with the <c>origin/</c> prefix stripped (empty when none).
    /// </summary>
    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>git ls-remote --heads {remote} {pattern}</c>. Returns the lines
    /// reported by ls-remote (each "{sha}\trefs/heads/{name}") — callers
    /// usually only care whether the list is non-empty.
    /// </summary>
    Task<IReadOnlyList<string>> LsRemoteHeadsAsync(string remote, string pattern, CancellationToken ct = default);

    /// <summary>
    /// <c>git rev-parse --verify refs/heads/{branch}</c>. Returns the commit
    /// SHA if the local branch exists, null otherwise.
    /// </summary>
    Task<string?> RevParseLocalBranchAsync(string branch, CancellationToken ct = default);

    /// <summary>
    /// <c>git checkout {branch}</c>. Throws <see cref="ExternalToolException"/>
    /// on failure (e.g. branch doesn't exist, dirty worktree conflicts).
    /// </summary>
    Task CheckoutAsync(string branch, CancellationToken ct = default);

    /// <summary>
    /// <c>git checkout -b {branch} [{startPoint}]</c>. Creates a new branch
    /// at the given start point (defaults to HEAD) and switches to it.
    /// Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task CreateBranchAsync(string branch, string? startPoint = null, CancellationToken ct = default);

    /// <summary>
    /// <c>git checkout --track origin/{branch}</c>. Creates a local tracking
    /// branch from the remote and switches to it.
    /// Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task CheckoutTrackingAsync(string branch, string remote = "origin", CancellationToken ct = default);

    /// <summary>
    /// <c>git push -u {remote} {branch}</c>. Pushes the branch to the remote
    /// and sets upstream tracking. Throws <see cref="ExternalToolException"/>
    /// on failure.
    /// </summary>
    Task PushAsync(string branch, string remote = "origin", CancellationToken ct = default);

    /// <summary>
    /// <c>git push {remote} --delete {branch}</c>. Deletes a branch from the
    /// remote. Returns the raw <see cref="ProcessResult"/> so callers can
    /// distinguish "branch did not exist" (best-effort 1 with stderr) from
    /// "delete failed" (any other non-zero) without losing diagnostic detail.
    /// Does not throw on non-zero exit; only throws if the runner itself
    /// fails (e.g. git binary missing).
    /// <para>Used by the P9 cascade-remedy <c>recreate</c> path to clean up
    /// the head branch of the closed PR before opening a fresh PR. Branch
    /// deletion failure on that path is logged as a warning, not a terminal
    /// error, so the verb consumes <see cref="ProcessResult.Succeeded"/>
    /// directly rather than relying on an exception.</para>
    /// </summary>
    Task<ProcessResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct = default);

    /// <summary>
    /// <c>git fetch {remote} {refspec}</c>. Fetches a specific branch/ref from
    /// the remote. Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task FetchAsync(string remote, string refspec, CancellationToken ct = default);

    /// <summary>
    /// <c>git status --porcelain</c>. Returns one entry per non-ignored
    /// path in a non-clean state (modified, untracked, staged, etc.). An
    /// empty list means the worktree is clean. Throws
    /// <see cref="ExternalToolException"/> on unexpected non-zero exit.
    /// </summary>
    Task<IReadOnlyList<string>> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>git add {pathspec}</c>. Stages the supplied path (file or
    /// directory). Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task StageAsync(string pathspec, CancellationToken ct = default);

    /// <summary>
    /// <c>git commit -m {message}</c>. Creates a commit with the supplied
    /// message. Throws <see cref="ExternalToolException"/> on failure
    /// (e.g. nothing staged, hook failure).
    /// </summary>
    Task CommitAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// <c>git reset --hard {refspec}</c>. Hard-resets the worktree and
    /// index to the supplied ref (e.g. <c>origin/feature/123</c>).
    /// Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task ResetHardAsync(string refspec, CancellationToken ct = default);

    /// <summary>
    /// <c>git show {refspec}:{path}</c>. Reads a file's contents at the
    /// supplied revision without disturbing the worktree. Returns null
    /// when the file does not exist at that revision; throws
    /// <see cref="ExternalToolException"/> on other non-zero exits
    /// (bad ref, repo error, etc.).
    /// </summary>
    Task<string?> ShowFileAtRefAsync(string refspec, string path, CancellationToken ct = default);

    /// <summary>
    /// <c>git worktree add -b {branch} {path} [{gitRef}]</c>. Creates a
    /// new linked worktree at <paramref name="path"/> with a freshly
    /// created branch <paramref name="branch"/> pointed at
    /// <paramref name="gitRef"/> (defaults to <c>HEAD</c> when null).
    ///
    /// <para>Returns the raw <see cref="ProcessResult"/> so callers can
    /// branch on success/failure without losing stderr — used by routing
    /// verbs that surface git's diagnostics inline rather than throwing.</para>
    /// </summary>
    Task<ProcessResult> WorktreeAddAsync(string branch, string path, string? gitRef, CancellationToken ct = default);

    /// <summary>
    /// <c>git worktree remove [--force] {path}</c>. Removes the linked
    /// worktree at <paramref name="path"/>. Pass <paramref name="force"/>
    /// to allow removal of dirty worktrees.
    ///
    /// <para>Returns the raw <see cref="ProcessResult"/> so callers can
    /// branch on success/failure without losing stderr.</para>
    /// </summary>
    Task<ProcessResult> WorktreeRemoveAsync(string path, bool force, CancellationToken ct = default);

    /// <summary>
    /// <c>git worktree list --porcelain</c>. Returns the raw porcelain
    /// output for callers to parse. Returned as a <see cref="ProcessResult"/>
    /// so the command verb can route on git's exit code rather than throw.
    /// </summary>
    Task<ProcessResult> WorktreeListAsync(CancellationToken ct = default);

    /// <summary>
    /// Detach onto <paramref name="head"/> and run
    /// <c>git rebase --onto {newBase} {oldBase} HEAD</c>. The rebase is
    /// expressed in explicit three-arg form (rather than
    /// <c>git rebase {newBase}</c>) so we replay only the commits that
    /// belong to the descendant — anything between <paramref name="oldBase"/>
    /// and <paramref name="head"/> — without picking up commits
    /// <paramref name="newBase"/> may have introduced beyond
    /// <paramref name="oldBase"/>.
    ///
    /// <para><b>Caller contract:</b> the worktree MUST be clean before
    /// invocation (no uncommitted changes, no in-progress rebase). The
    /// caller is responsible for verifying that — this method does NOT
    /// run <c>git status</c> defensively.</para>
    ///
    /// <para>Outcomes are returned as a <see cref="RebaseOutcome"/>
    /// discriminated union. On <see cref="RebaseOutcome.Conflict"/> /
    /// <see cref="RebaseOutcome.Failed"/> the implementation runs
    /// <c>git rebase --abort</c> before returning, leaving the worktree in
    /// detached-HEAD state at <paramref name="head"/>.</para>
    /// </summary>
    /// <param name="newBase">Target ref to rebase onto (e.g. <c>origin/plan/100</c>).</param>
    /// <param name="oldBase">SHA of the commit the head was previously based on (typically <c>git merge-base HEAD origin/{old-parent}</c>).</param>
    /// <param name="head">SHA of the head commit being rebased. Checked out detached before rebasing.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RebaseOutcome> RebaseOntoAsync(
        string newBase,
        string oldBase,
        string head,
        CancellationToken ct = default);

    /// <summary>
    /// <c>git merge-base {a} {b}</c>. Returns the trimmed SHA of the best
    /// common ancestor, or null when no merge base exists (disconnected
    /// histories, exit code 1) or when the call fails for any other reason.
    /// Used by the cascade remedy to compute the <c>oldBase</c> argument
    /// for <see cref="RebaseOntoAsync"/>.
    /// </summary>
    Task<string?> MergeBaseAsync(string a, string b, CancellationToken ct = default);

    /// <summary>
    /// <c>git merge-base --is-ancestor {maybeAncestor} {descendant}</c>.
    /// Returns true when git exits 0 (is an ancestor), false on exit 1
    /// (not an ancestor — the documented "boolean false" exit). Throws
    /// <see cref="ExternalToolException"/> on any other exit code (bad ref,
    /// repo error). Used by the three-fact freshness check to decide whether
    /// the parent's tip is already an ancestor of the head we're considering.
    /// </summary>
    Task<bool> IsAncestorAsync(string maybeAncestor, string descendant, CancellationToken ct = default);

    /// <summary>
    /// <c>git push {remote} HEAD:refs/heads/{branch} --force-with-lease=refs/heads/{branch}:{expectedRemoteSha}</c>.
    /// Pushes the worktree's current HEAD (typically a detached-HEAD post-rebase
    /// commit) to <paramref name="branch"/> on <paramref name="remote"/> with a
    /// strict lease — git refuses the push if the remote no longer points at
    /// <paramref name="expectedRemoteSha"/>, so a concurrent push by another
    /// actor cannot be silently overwritten.
    ///
    /// <para><b>Returns the raw <see cref="ProcessResult"/></b> so the
    /// cascade-remedy verb can route on git's exit code and stderr without
    /// catching exceptions: a lease failure (stderr contains "stale info" or
    /// "rejected") becomes <c>pr_head_changed</c> in the verb output, while
    /// any other non-zero exit is surfaced as <c>git_failed</c> with the raw
    /// stderr for diagnostics.</para>
    ///
    /// <para>Caller contract: HEAD must already be at the commit you want to
    /// push. This method does not run any pre-push checks.</para>
    /// </summary>
    /// <param name="remote">Remote name, typically <c>origin</c>.</param>
    /// <param name="branch">Target branch name on the remote (without <c>refs/heads/</c> prefix).</param>
    /// <param name="expectedRemoteSha">SHA the remote ref is expected to point at. The push fails if the remote no longer matches.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessResult> PushHeadWithLeaseAsync(
        string remote,
        string branch,
        string expectedRemoteSha,
        CancellationToken ct = default);
}
