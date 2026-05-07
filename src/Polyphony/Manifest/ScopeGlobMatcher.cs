namespace Polyphony.Manifest;

/// <summary>
/// Phase 3 P8: minimal glob matcher for the <c>plan validate-scope</c>
/// verb. Supports the subset needed to express "child planner is
/// authorized to touch these paths":
/// <list type="bullet">
///   <item><c>?</c> — exactly one character (NOT a path separator).</item>
///   <item><c>*</c> — zero or more characters within a single path
///     segment (does NOT cross path separators).</item>
///   <item><c>**</c> — zero or more characters INCLUDING path separators
///     (cross-segment). Must appear as a complete path segment, e.g.
///     <c>plans/1100/**</c> or <c>**/foo.md</c> — not <c>pla**ns</c>.</item>
///   <item>Everything else — literal characters (case-sensitive; paths
///     are repo-relative forward-slash, matching <c>gh pr view --json files</c>'s
///     output convention).</item>
/// </list>
///
/// <para>We deliberately avoid <see cref="System.IO.Enumeration.FileSystemName"/>
/// because its <c>*</c> crosses path separators (file-system semantics,
/// not shell-glob semantics) which would silently widen the in-scope set
/// in surprising ways. Phase 3 prefers the conservative posix-glob-ish
/// interpretation; if a workflow really wants "anything under here", it
/// can spell it <c>**</c>.</para>
/// </summary>
public static class ScopeGlobMatcher
{
    /// <summary>
    /// Returns true iff <paramref name="path"/> matches <paramref name="glob"/>.
    /// <paramref name="path"/> is treated as repo-relative forward-slash.
    /// Empty <paramref name="glob"/> never matches (defensive — callers
    /// should validate before calling).
    /// </summary>
    public static bool IsMatch(string path, string glob)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(glob)) return false;
        return MatchSegment(path, 0, glob, 0);
    }

    /// <summary>
    /// Returns true iff <paramref name="path"/> matches at least one of
    /// <paramref name="globs"/>. An empty <paramref name="globs"/> list
    /// returns false (nothing is in scope).
    /// </summary>
    public static bool IsMatchAny(string path, IReadOnlyList<string> globs)
    {
        if (globs.Count == 0) return false;
        for (int i = 0; i < globs.Count; i++)
        {
            if (IsMatch(path, globs[i])) return true;
        }
        return false;
    }

    // Recursive matcher with backtracking on '*' and '**'. Path and glob
    // are both forward-slash strings; we walk both with explicit indexes
    // rather than allocating substrings.
    private static bool MatchSegment(string path, int pi, string glob, int gi)
    {
        while (gi < glob.Length)
        {
            var gc = glob[gi];

            if (gc == '*')
            {
                bool isDoubleStar = gi + 1 < glob.Length && glob[gi + 1] == '*';

                if (isDoubleStar)
                {
                    // Skip past the '**'. Then optionally consume a
                    // trailing '/' so '**/' acts as "zero or more
                    // segments" (matches both 'foo' and 'a/foo').
                    int afterStar = gi + 2;
                    if (afterStar < glob.Length && glob[afterStar] == '/')
                    {
                        // Try matching with '**/' consuming nothing first
                        // (so 'a/**/b.md' matches 'a/b.md').
                        if (MatchSegment(path, pi, glob, afterStar + 1)) return true;
                        // Otherwise, '**' can absorb any prefix of path
                        // including separators.
                        for (int j = pi; j < path.Length; j++)
                        {
                            if (path[j] == '/' && MatchSegment(path, j + 1, glob, afterStar + 1))
                                return true;
                        }
                        return false;
                    }
                    // '**' at end of pattern, or '**' followed by something
                    // other than '/'. Either way, absorb everything
                    // remaining and try matching the tail.
                    for (int j = pi; j <= path.Length; j++)
                    {
                        if (MatchSegment(path, j, glob, afterStar)) return true;
                    }
                    return false;
                }

                // Single '*' — match zero-or-more chars within one segment.
                int afterSingle = gi + 1;
                for (int j = pi; j <= path.Length; j++)
                {
                    if (MatchSegment(path, j, glob, afterSingle)) return true;
                    if (j < path.Length && path[j] == '/') break;
                }
                return false;
            }

            if (gc == '?')
            {
                if (pi >= path.Length || path[pi] == '/') return false;
                pi++; gi++;
                continue;
            }

            // Literal char (including '/'). Case-sensitive.
            if (pi >= path.Length || path[pi] != gc) return false;
            pi++; gi++;
        }

        return pi == path.Length;
    }
}
