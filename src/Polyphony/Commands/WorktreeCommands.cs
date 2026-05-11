using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// Worktree management verbs (<c>polyphony worktree ...</c>) — thin
/// routing wrappers around <c>git worktree</c> that emit structured JSON
/// for workflow consumers.
///
/// <para>Verbs in this group are pure infrastructure for the upcoming
/// Phase 7 wiring: callers never see git's stderr directly, and the verbs
/// always exit 0 so workflows route on the <c>error</c> field rather than
/// on shell exit codes.</para>
///
/// <list type="bullet">
///   <item><see cref="Add"/>          — wraps <c>git worktree add -b B P [R]</c></item>
///   <item><see cref="Remove"/>       — wraps <c>git worktree remove [--force] P</c></item>
///   <item><see cref="List"/>         — wraps <c>git worktree list --porcelain</c></item>
///   <item><see cref="Status"/>       — reports cleanliness + current branch of a worktree</item>
///   <item><see cref="AssertClean"/>  — pre-flight gate: clean + (optionally) on expected branch</item>
/// </list>
/// </summary>
[VerbGroup("worktree")]
public sealed partial class WorktreeCommands(IGitClient git)
{
    private readonly IGitClient _git = git;
}
