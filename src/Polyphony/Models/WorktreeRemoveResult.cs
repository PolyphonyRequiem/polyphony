namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree remove</c>. Echoes the requested
/// path and force flag, and surfaces git's stderr in <see cref="Error"/>
/// on failure.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch
/// on whether <see cref="Error"/> is populated.</para>
/// </summary>
public sealed record WorktreeRemoveResult
{
    /// <summary>Filesystem path of the worktree that was removed.</summary>
    public required string Path { get; init; }

    /// <summary>True when <c>--force</c> was passed (git allows dirty worktrees).</summary>
    public required bool Force { get; init; }

    /// <summary>Error message on failure (git stderr); null on success.</summary>
    public string? Error { get; init; }
}
