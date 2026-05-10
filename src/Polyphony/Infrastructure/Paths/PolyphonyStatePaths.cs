using System.Globalization;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Infrastructure.Paths;

/// <summary>
/// Resolves the absolute on-disk path for polyphony per-root state — the
/// run manifest, the run lock, and any future per-root operational files.
/// State is rooted under the git common directory (NOT the worktree) so
/// every linked worktree of the same clone converges on the same files
/// while concurrent runs against different roots remain isolated by the
/// per-root subdirectory.
///
/// <para>Layout (per the Rev 4.2 amendment to <c>docs/decisions/branch-model.md</c>):</para>
/// <code>
/// &lt;git-common-dir&gt;/polyphony/&lt;root_id&gt;/
///   run.yaml
///   locks/
///     run.lock
/// </code>
///
/// <para><b>Why the common dir?</b> Through Rev 4.1 the manifest lived at
/// <c>.polyphony/run.yaml</c> tracked in git on the feature branch. The
/// AB#3067 dogfood found that <c>git worktree add</c> from main carried
/// the *previous* run's manifest into every fresh worktree, tripping
/// <c>manifest_root_mismatch</c> at preflight. Untracked, common-dir
/// state eliminates the bleed at the source.</para>
///
/// <para><b>Why <c>--git-common-dir</c> with <c>--path-format=absolute</c>?</b>
/// In linked worktrees, <c>--git-dir</c> resolves to
/// <c>.git/worktrees/{name}</c>; only <c>--git-common-dir</c> resolves
/// to the *shared* directory that the main and every linked worktree
/// see. The default output is relative to cwd, which silently produces
/// inconsistent absolute paths between worktrees — <c>--path-format=absolute</c>
/// is mandatory. See <see cref="IGitClient.GetCommonDirAsync"/>.</para>
///
/// <para><b>Throws when not in a git repo.</b> All resolver methods throw
/// <see cref="InvalidOperationException"/> when
/// <see cref="IGitClient.GetCommonDirAsync"/> returns null. Production
/// callers are inside a worktree by construction; tests bypass the
/// resolver entirely by passing an explicit <c>--path</c> argument to
/// the verb (the testing seam) rather than mocking this layer.</para>
/// </summary>
public sealed class PolyphonyStatePaths
{
    /// <summary>Subdirectory under the git common dir that holds all polyphony state.</summary>
    public const string StateSubdirName = "polyphony";

    /// <summary>Lock subdirectory under each per-root state directory.</summary>
    public const string LocksSubdirName = "locks";

    /// <summary>Manifest file name under each per-root state directory.</summary>
    public const string ManifestFileName = "run.yaml";

    /// <summary>Run-lock file name under the per-root locks directory.</summary>
    public const string LockFileName = "run.lock";

    private readonly IGitClient _git;

    public PolyphonyStatePaths(IGitClient git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>
    /// Returns the polyphony state base directory:
    /// <c>&lt;git-common-dir&gt;/polyphony/</c>. No directories are created.
    /// </summary>
    public async Task<string> GetStateBaseAsync(CancellationToken ct = default)
    {
        var commonDir = await ResolveCommonDirAsync(ct).ConfigureAwait(false);
        return Path.Combine(commonDir, StateSubdirName);
    }

    /// <summary>
    /// Returns the per-root state directory:
    /// <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/</c>. No directories
    /// are created.
    /// </summary>
    public async Task<string> GetStateRootAsync(int rootId, CancellationToken ct = default)
    {
        ValidateRootId(rootId);
        var stateBase = await GetStateBaseAsync(ct).ConfigureAwait(false);
        return Path.Combine(stateBase, rootId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Returns the per-root manifest file path:
    /// <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/run.yaml</c>. No
    /// directories are created.
    /// </summary>
    public async Task<string> GetManifestPathAsync(int rootId, CancellationToken ct = default)
    {
        var stateRoot = await GetStateRootAsync(rootId, ct).ConfigureAwait(false);
        return Path.Combine(stateRoot, ManifestFileName);
    }

    /// <summary>
    /// Returns the per-root lock file path:
    /// <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/locks/run.lock</c>.
    /// No directories are created — the lock-store layer is responsible
    /// for ensuring the parent directory exists before writing.
    /// </summary>
    public async Task<string> GetLockPathAsync(int rootId, CancellationToken ct = default)
    {
        var stateRoot = await GetStateRootAsync(rootId, ct).ConfigureAwait(false);
        return Path.Combine(stateRoot, LocksSubdirName, LockFileName);
    }

    private async Task<string> ResolveCommonDirAsync(CancellationToken ct)
    {
        var commonDir = await _git.GetCommonDirAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(commonDir))
        {
            throw new InvalidOperationException(
                "polyphony state requires a git repository: 'git rev-parse --path-format=absolute --git-common-dir' returned no path. " +
                "Run polyphony from inside a clone, or pass an explicit --path to the manifest/lock verb (the testing seam).");
        }
        return commonDir;
    }

    private static void ValidateRootId(int rootId)
    {
        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootId),
                rootId,
                "rootId must be a positive integer (the apex work-item id).");
        }
    }
}
