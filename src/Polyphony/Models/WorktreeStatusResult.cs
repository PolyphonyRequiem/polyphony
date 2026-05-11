namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree status</c>. Reports the cleanliness and
/// current branch of a single worktree path, plus the raw porcelain lines
/// for any dirty entries.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch on
/// <see cref="IsClean"/> / <see cref="Error"/>. <see cref="DirtyPaths"/> is
/// the verbatim line-per-entry output of <c>git status --porcelain</c> (each
/// prefixed with the two-character index/worktree status pair) so callers
/// can surface a precise diagnostic without re-running git.</para>
/// </summary>
public sealed record WorktreeStatusResult
{
    /// <summary>Absolute filesystem path of the worktree the verb queried.</summary>
    public required string Path { get; init; }

    /// <summary>True when <c>git status --porcelain</c> returned no entries.</summary>
    public required bool IsClean { get; init; }

    /// <summary>
    /// Current branch of the worktree (<c>refs/heads/</c> prefix stripped),
    /// or null when HEAD is detached.
    /// </summary>
    public string? CurrentBranch { get; init; }

    /// <summary>
    /// Raw porcelain lines for each non-clean entry; empty list when
    /// <see cref="IsClean"/> is true. Each line carries git's two-character
    /// XY status prefix.
    /// </summary>
    public required IReadOnlyList<string> DirtyPaths { get; init; }

    /// <summary>Error message on failure (git stderr); null on success.</summary>
    public string? Error { get; init; }
}
