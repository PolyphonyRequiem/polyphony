namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree add</c>. Echoes the requested
/// branch/path/ref so callers can confirm the verb's interpretation,
/// and surfaces git's stderr in <see cref="Error"/> on failure.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch
/// on whether <see cref="Error"/> is populated.</para>
/// </summary>
public sealed record WorktreeAddResult
{
    /// <summary>Branch the worktree was created with (passed to <c>git worktree add -b</c>).</summary>
    public required string Branch { get; init; }

    /// <summary>Filesystem path the worktree was created at.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Git ref the new branch was rooted at. Null when the verb was
    /// invoked without <c>--ref</c> — git defaults to <c>HEAD</c>.
    /// </summary>
    public string? GitRef { get; init; }

    /// <summary>Error message on failure (git stderr); null on success.</summary>
    public string? Error { get; init; }
}
