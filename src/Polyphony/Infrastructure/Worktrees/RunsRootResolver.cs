namespace Polyphony.Infrastructure.Worktrees;

/// <summary>
/// Resolves the per-run worktree root (<c>polyphony-runs/</c>) and the
/// conventional main-worktree path from a <c>git rev-parse --git-common-dir</c>
/// output. Pure function; no I/O.
///
/// <para>Given the AB#3085 layout convention:</para>
/// <code>
/// ~/projects/polyphony.git/             # bare gitdir (common-dir)
/// ~/projects/polyphony/                 # main worktree (sibling of the bare)
/// ~/projects/polyphony-runs/            # per-run worktree root
/// </code>
///
/// <para>...both bare and non-bare layouts converge on the same
/// <c>{parent}/{repo_basename}-runs/</c> runs root. The main-worktree path
/// is the conventional sibling at <c>{parent}/{repo_basename}/</c>; for
/// non-bare layouts it is the parent of the supplied <c>.git</c>
/// directory. Callers consume the main-worktree path purely as a
/// safety check (do not write into it); they MUST NOT assume a worktree
/// actually exists at that path.</para>
///
/// <para>Used by <c>polyphony worktree init-apex</c> and
/// <c>polyphony worktree create</c> (AB#3085, PR 1b2 / PR 1b3).</para>
/// </summary>
public static class RunsRootResolver
{
    /// <summary>
    /// Resolve the runs root and conventional main-worktree path from the
    /// supplied common-dir.
    /// </summary>
    /// <param name="commonDir">
    /// Absolute path to the shared git directory, as returned by
    /// <c>git rev-parse --path-format=absolute --git-common-dir</c>. Must be
    /// non-empty.
    /// </param>
    /// <returns>
    /// A tuple of <c>(runsRoot, mainWorktreePath)</c>, both absolute and
    /// canonicalized via <see cref="Path.GetFullPath(string)"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="commonDir"/> is null/empty, or when the
    /// path has no parent directory (root-level paths cannot host a sibling
    /// runs root).
    /// </exception>
    public static (string RunsRoot, string MainWorktreePath) Resolve(string commonDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(commonDir);

        // Canonicalize first so trailing slashes / `..` segments / mixed
        // separators do not perturb the basename / parent split below.
        var normalized = Path.GetFullPath(commonDir).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        var basename = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(basename))
        {
            throw new ArgumentException(
                $"common-dir has no basename (likely a drive root): '{commonDir}'",
                nameof(commonDir));
        }

        // Two layouts:
        //   non-bare: <repo_dir>/.git   → repo_basename = basename(repo_dir)
        //   bare:     <parent>/<name>.git → repo_basename = name
        // The non-bare ".git" suffix is recognised by EXACT match on the
        // basename — a bare repo named literally ".git" would be an
        // operator pathology we do not need to support.
        string repoDir;
        if (string.Equals(basename, ".git", StringComparison.Ordinal))
        {
            // Non-bare: parent of `.git` IS the repo dir.
            repoDir = Path.GetDirectoryName(normalized)
                ?? throw new ArgumentException(
                    $"common-dir '{commonDir}' has no parent directory",
                    nameof(commonDir));
        }
        else if (basename.EndsWith(".git", StringComparison.Ordinal))
        {
            // Bare: <parent>/<name>.git → conventional main worktree at <parent>/<name>.
            var parent = Path.GetDirectoryName(normalized)
                ?? throw new ArgumentException(
                    $"bare common-dir '{commonDir}' has no parent directory",
                    nameof(commonDir));
            var name = basename[..^".git".Length];
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    $"bare common-dir '{commonDir}' has empty repo name (basename is just '.git')",
                    nameof(commonDir));
            }
            repoDir = Path.Combine(parent, name);
        }
        else
        {
            // Some operators host the gitdir under an unusual name (rare);
            // treat the basename verbatim as the repo basename and use the
            // common-dir's parent as the runs-root parent. This keeps the
            // invariant `runs_root == sibling-of-repo` regardless of layout.
            repoDir = normalized;
        }

        var repoParent = Path.GetDirectoryName(repoDir)
            ?? throw new ArgumentException(
                $"resolved repo dir '{repoDir}' has no parent directory",
                nameof(commonDir));
        var repoBasename = Path.GetFileName(repoDir);
        if (string.IsNullOrEmpty(repoBasename))
        {
            throw new ArgumentException(
                $"resolved repo dir '{repoDir}' has empty basename",
                nameof(commonDir));
        }

        var runsRoot = Path.GetFullPath(Path.Combine(repoParent, repoBasename + "-runs"));
        var mainWorktree = Path.GetFullPath(repoDir);
        return (runsRoot, mainWorktree);
    }
}
