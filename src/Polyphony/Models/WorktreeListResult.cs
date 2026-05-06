namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree list</c>. Carries the parsed
/// <see cref="WorktreeEntry"/> list (empty when git reported no
/// worktrees) and surfaces git's stderr in <see cref="Error"/> when
/// invocation or parsing failed.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch
/// on whether <see cref="Error"/> is populated.</para>
/// </summary>
public sealed record WorktreeListResult
{
    /// <summary>Parsed worktree entries; empty on failure or when git reported none.</summary>
    public required IReadOnlyList<WorktreeEntry> Worktrees { get; init; }

    /// <summary>Error message on failure (git stderr or parse error); null on success.</summary>
    public string? Error { get; init; }
}
