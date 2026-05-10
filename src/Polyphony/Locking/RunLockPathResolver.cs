using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Locking;

/// <summary>
/// Resolves the on-disk path for a polyphony run lock.
/// Layout (per the Rev 4.2 amendment to the branch-model ADR):
/// <c>&lt;git-common-dir&gt;/polyphony/&lt;root_id&gt;/locks/run.lock</c>.
///
/// <para>Lock-path resolution delegates to
/// <see cref="PolyphonyStatePaths"/>, which throws when not inside a
/// git repo. Repo root (used only to populate
/// <see cref="RunLock.RepoRoot"/> for human-readable diagnostics)
/// continues to be resolved from
/// <see cref="IGitClient.GetTopLevelAsync"/> with a cwd fallback —
/// it is not load-bearing.</para>
///
/// <para>Callers can still override the entire path for tests via
/// the <c>--path</c> flag on each lock verb.</para>
/// </summary>
public sealed class RunLockPathResolver
{
    private readonly IGitClient _git;
    private readonly PolyphonyStatePaths _paths;

    public RunLockPathResolver(IGitClient git)
        : this(git, new PolyphonyStatePaths(git))
    {
    }

    public RunLockPathResolver(IGitClient git, PolyphonyStatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(paths);
        _git = git;
        _paths = paths;
    }

    /// <summary>
    /// Returns the absolute lock-file path for the given root id.
    /// Creates no directories. Throws when not inside a git repo.
    /// </summary>
    public Task<string> ResolveAsync(int rootId, CancellationToken ct = default)
        => _paths.GetLockPathAsync(rootId, ct);

    /// <summary>
    /// Returns the absolute repo root for human-readable diagnostics in
    /// <see cref="RunLock.RepoRoot"/>. Falls back to the current working
    /// directory when not inside a git repo — this field is informational
    /// only and never used for path resolution.
    /// </summary>
    public async Task<string> ResolveRepoRootAsync(CancellationToken ct = default)
    {
        return await _git.GetTopLevelAsync(ct).ConfigureAwait(false)
            ?? Directory.GetCurrentDirectory();
    }
}
