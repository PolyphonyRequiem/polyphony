namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Outcome of <see cref="IGitClient.RebaseOntoAsync"/>. Discriminated union
/// rather than null/throw so callers can route on the three meaningfully
/// different failure modes (clean / merge conflict / hard failure)
/// without sniffing exception messages.
///
/// <para>The cascade-remedy verb consumes this directly:
/// <list type="bullet">
///   <item><see cref="Clean"/> → push the new HEAD, update PR body, record
///     the rebase in the manifest, post the comment.</item>
///   <item><see cref="Conflict"/> → leave the worktree in a clean
///     detached-HEAD state (the implementation runs <c>git rebase --abort</c>
///     defensively) and route the PR to <c>human_gate</c>.</item>
///   <item><see cref="Failed"/> → escalate as <c>rebase_failed</c>; surfaces
///     the captured stderr so the workflow output is diagnosable.</item>
/// </list>
/// </para>
/// </summary>
public abstract record RebaseOutcome
{
    /// <summary>
    /// Rebase completed without conflicts. The worktree is checked out at
    /// <see cref="NewHeadSha"/> in detached-HEAD state — the caller is
    /// expected to push directly from HEAD with <c>--force-with-lease</c>.
    /// </summary>
    public sealed record Clean(string NewHeadSha) : RebaseOutcome;

    /// <summary>
    /// Rebase produced merge conflicts. The implementation MUST have
    /// already run <c>git rebase --abort</c> before returning, so the
    /// worktree is back in detached-HEAD state at the original commit
    /// with no <c>.git/rebase-merge</c> / <c>.git/rebase-apply</c> directories.
    /// <see cref="Files"/> lists the conflicted paths parsed from git's
    /// "CONFLICT" output for diagnostics.
    /// </summary>
    public sealed record Conflict(IReadOnlyList<string> Files) : RebaseOutcome;

    /// <summary>
    /// Rebase failed for a reason other than merge conflicts (bad ref,
    /// hook failure, etc.). The implementation runs <c>git rebase --abort</c>
    /// defensively before returning. <see cref="Stderr"/> carries git's
    /// captured stderr for the workflow's error-routing output.
    /// </summary>
    public sealed record Failed(string Stderr) : RebaseOutcome;
}
