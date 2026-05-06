using Polyphony.Infrastructure.Processes;

namespace Polyphony.Locking;

/// <summary>
/// Resolves the on-disk path for a polyphony run lock.
/// Default layout (per the Rev 4 branch-model ADR):
/// <c>&lt;repoRoot&gt;/.polyphony/locks/run-{root_id}.lock</c>.
///
/// <para>Repo root is resolved via <see cref="IGitClient.GetTopLevelAsync"/>
/// when available; falls back to the current working directory when
/// not inside a git repo. Callers can override the entire path for
/// tests.</para>
/// </summary>
public sealed class RunLockPathResolver
{
    private readonly IGitClient _git;

    public RunLockPathResolver(IGitClient git)
    {
        _git = git;
    }

    /// <summary>
    /// Returns the absolute lock-file path for the given root id.
    /// Creates no directories.
    /// </summary>
    public async Task<string> ResolveAsync(int rootId, CancellationToken ct = default)
    {
        var repoRoot = await _git.GetTopLevelAsync(ct).ConfigureAwait(false)
            ?? Directory.GetCurrentDirectory();

        return Path.Combine(repoRoot, ".polyphony", "locks", $"run-{rootId}.lock");
    }

    /// <summary>
    /// Returns the absolute repo root used for default lock-path
    /// resolution. Used by acquire to populate
    /// <see cref="RunLock.RepoRoot"/> for human-readable diagnostics.
    /// </summary>
    public async Task<string> ResolveRepoRootAsync(CancellationToken ct = default)
    {
        return await _git.GetTopLevelAsync(ct).ConfigureAwait(false)
            ?? Directory.GetCurrentDirectory();
    }
}
