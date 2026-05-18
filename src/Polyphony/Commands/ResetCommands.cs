using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Commands;

/// <summary>
/// Reset-family verbs (<c>polyphony reset ...</c>) — the
/// re-dispatch-safety primitives that complement the run-watermark
/// observer filter shipped in PR 1
/// (<see cref="Polyphony.Tagging.PolyphonyTags.RunStartedAtPrefix"/>).
///
/// <para>The full design lives in <c>docs/decisions/run-reset.md</c>;
/// each verb's doc-comment summarises the contract specific to that
/// step. Common contract across the whole family:</para>
///
/// <list type="bullet">
///   <item><b>Dry-run default.</b> Every verb runs in dry-run mode unless
///         the caller passes <c>--execute</c>. Dry-run emits the
///         would-be outcome envelope with no mutations.</item>
///   <item><b>Routing-style envelope.</b> Always exits 0. Callers route
///         on <c>Success</c> + <c>Error</c> in the JSON payload.</item>
///   <item><b>Per-item failure tolerance.</b> A handful of failed items
///         (a PR the platform refuses to abandon, a worktree git can't
///         remove) does not poison the run — those surface as entries in
///         the per-step <c>Failed*</c> list with the verb still
///         reporting <c>Success</c> = true. Hard failures (identity
///         resolution crashed, twig sync blew up) set <c>Success</c> =
///         false.</item>
///   <item><b>Idempotent.</b> Re-running any verb after a partial run
///         must converge on the same terminal state. The state writer
///         (<see cref="ResetState"/>) is the single source of truth for
///         the watermark; the others are purely best-effort sweeps.</item>
/// </list>
///
/// <para><b>Composite ordering</b> (see <see cref="ResetApex"/>):
/// PRs → worktrees → branches → manifest → state. The state stamp lands
/// LAST so that a crash anywhere in the cleanup chain leaves the system
/// "still mid-reset" rather than "watermark advanced but PRs/branches
/// still leak past it".</para>
/// </summary>
[VerbGroup("reset")]
public sealed partial class ResetCommands(
    ITwigClient twig,
    IGitClient git,
    PullRequestReader pullRequestReader,
    PlanObserver planObserver,
    Polyphony.Routing.HierarchyWalker walker)
{
    private readonly ITwigClient _twig = twig;
    private readonly IGitClient _git = git;
    private readonly PullRequestReader _pullRequestReader = pullRequestReader;
    private readonly PlanObserver _planObserver = planObserver;
    private readonly Polyphony.Routing.HierarchyWalker _walker = walker;

    /// <summary>
    /// Canonical apex-scoped branch prefix set. Used by <c>reset prs</c>
    /// and <c>reset branches</c> to enumerate every branch the polyphony
    /// pipeline may have created for an apex.
    ///
    /// <para>Patterns are passed to <c>git ls-remote --heads origin {pattern}</c>
    /// where <c>refs/heads/</c> is prepended; for purely-local enumeration
    /// the patterns are used with <c>git for-each-ref</c>.</para>
    ///
    /// <para>The trailing <c>-*</c> on impl/mg/evidence accommodates the
    /// <c>{root}-{item}</c> and <c>{root}-{pgPath}</c> shapes documented
    /// in <c>docs/decisions/branch-model.md</c>.</para>
    /// </summary>
    internal static IReadOnlyList<string> ApexBranchPatterns(int apex)
    {
        var apexStr = apex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [
            $"plan/{apexStr}",
            // MG branches use `_` between root_id and mg_path (see
            // docs/decisions/branch-model.md §Branch names: `mg/{root_id}_{mg_path}`).
            // The `_` is unambiguous because mg_id segments match
            // `^[a-z][a-z0-9-]{0,30}$`, which excludes `_`. Earlier
            // revisions used `-` here, which silently failed to match
            // any MG branch and left mg/* refs on origin after reset.
            $"mg/{apexStr}_*",
            $"impl/{apexStr}-*",
            $"evidence/{apexStr}-*",
            $"feature/{apexStr}",
        ];
    }
}
