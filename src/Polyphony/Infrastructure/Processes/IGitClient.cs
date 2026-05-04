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
}
