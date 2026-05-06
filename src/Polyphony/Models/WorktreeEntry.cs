namespace Polyphony;

/// <summary>
/// One worktree as parsed from <c>git worktree list --porcelain</c>.
/// Fields mirror git's porcelain block keys:
/// <list type="bullet">
///   <item><c>worktree</c> → <see cref="Path"/></item>
///   <item><c>HEAD</c> → <see cref="Head"/></item>
///   <item><c>branch refs/heads/{name}</c> → <see cref="Branch"/></item>
///   <item><c>bare</c> → <see cref="IsBare"/></item>
///   <item><c>detached</c> → <see cref="IsDetached"/></item>
/// </list>
/// </summary>
public sealed record WorktreeEntry
{
    /// <summary>Absolute filesystem path of the worktree.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Branch name with the <c>refs/heads/</c> prefix stripped, or null
    /// when the worktree is bare or detached.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>HEAD commit SHA; null for bare repositories.</summary>
    public string? Head { get; init; }

    /// <summary>True when the worktree is a bare repository.</summary>
    public bool IsBare { get; init; }

    /// <summary>True when HEAD is detached (not pointing at a branch).</summary>
    public bool IsDetached { get; init; }
}
