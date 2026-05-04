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
    /// <c>git fetch {remote} {refspec}</c>. Fetches a specific branch/ref from
    /// the remote. Throws <see cref="ExternalToolException"/> on failure.
    /// </summary>
    Task FetchAsync(string remote, string refspec, CancellationToken ct = default);
}
