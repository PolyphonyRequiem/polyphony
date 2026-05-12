using System.Runtime.InteropServices;

namespace Polyphony.Infrastructure.Worktrees;

/// <summary>
/// Boundary-aware path containment checks.
///
/// <para><b>Why this exists:</b> a naive <c>child.StartsWith(parent)</c>
/// is unsafe for filesystem paths. <c>C:\projects\polyphony-runs</c>
/// "starts with" <c>C:\projects\polyphony</c> as a string but is NOT
/// inside it as a directory. The bare-repo + per-run-worktree layout
/// (AB#3085) puts <c>polyphony</c> (main worktree) and <c>polyphony-runs</c>
/// (run roots) as siblings — so the naive check would falsely flag the
/// expected layout as a hijack.</para>
///
/// <para>This helper compares canonicalized paths and requires the child
/// either be exactly equal to the parent OR be followed by a directory
/// separator immediately after the parent's full length.</para>
///
/// <para>Used by the worktree write-verbs (<c>init-apex</c>, <c>create</c>)
/// to assert "self-derived target path is under runs_root AND not under
/// main_worktree_path" — the structural invariant that makes the
/// hijack bug impossible by construction.</para>
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="child"/> is the same path
    /// as <paramref name="parent"/> or a descendant of it. Inputs are
    /// canonicalized via <see cref="Path.GetFullPath(string)"/> first;
    /// trailing separators are normalized; comparison is case-insensitive
    /// on Windows and case-sensitive elsewhere.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="parent"/> or <paramref name="child"/>
    /// is null/empty.
    /// </exception>
    public static bool IsSameOrSubpath(string parent, string child)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        ArgumentException.ThrowIfNullOrEmpty(child);

        var p = Normalize(parent);
        var c = Normalize(child);
        var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(p, c, cmp)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, cmp);
    }

    private static string Normalize(string path)
    {
        // GetFullPath canonicalizes separators and resolves ../. segments.
        var full = Path.GetFullPath(path);
        // Trim ALL trailing directory separators so root paths ("C:\")
        // and "C:\foo\" both compare cleanly.
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
