using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Journal.Reset;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Commands;

/// <summary>
/// Reset-family verbs (<c>polyphony reset ...</c>) — the
/// re-dispatch-safety primitives that complement the run-watermark
/// observer filter shipped in PR 1
/// (<see cref="Polyphony.Tagging.PolyphonyTags.RunStartedAtPrefix"/>).
///
/// <para>Post-AB#3308 the verb group is a single verb,
/// <see cref="ResetRoot"/>, delegating to
/// <see cref="ProjectionResetExecutor"/>. The full design lives in
/// <c>docs/decisions/run-reset.md</c> and the strategy-retirement
/// rationale in <c>docs/decisions/pattern-strategy-retirement.md</c>.</para>
///
/// <list type="bullet">
///   <item><b>Dry-run default.</b> Runs in dry-run mode unless the
///         caller passes <c>--execute</c>.</item>
///   <item><b>Routing-style envelope.</b> Always exits 0; callers route
///         on <c>Success</c> + <c>Error</c> in the JSON payload.</item>
///   <item><b>Idempotent.</b> Re-running converges on the same terminal
///         state; the projection executor's coverage gate ensures
///         partial runs leave the system "still mid-reset" rather than
///         half-cleaned.</item>
/// </list>
/// </summary>
[VerbGroup("reset")]
public sealed partial class ResetCommands(
    ITwigClient twig,
    IGitClient git,
    PullRequestReader pullRequestReader,
    PlanObserver planObserver,
    Polyphony.Routing.HierarchyWalker walker,
    RunContext? runContext = null,
    JournaledActionDecorator? journalDecorator = null,
    ProjectionResetExecutor? projectionResetExecutor = null)
{
    private readonly ITwigClient _twig = twig;
    private readonly IGitClient _git = git;
    private readonly PullRequestReader _pullRequestReader = pullRequestReader;
    private readonly PlanObserver _planObserver = planObserver;
    private readonly Polyphony.Routing.HierarchyWalker _walker = walker;
    private readonly ProjectionResetExecutor? _projectionResetExecutor = projectionResetExecutor;
    private readonly RunContext _runContext = JournalCommandSupport.ResolveRunContext(runContext);
    private readonly JournaledActionDecorator _journalDecorator = JournalCommandSupport.ResolveDecorator(journalDecorator);

    /// <summary>
    /// Canonical PR-closure comment posted by the projection reset
    /// executor's PR deleters (<see cref="Polyphony.Journal.Reset.Deleters.AdoPrDeleter"/>
    /// and <see cref="Polyphony.Journal.Reset.Deleters.GitHubPrDeleter"/>)
    /// when no operator-supplied <c>--comment</c> override is in play.
    ///
    /// <para>Surfaces in the platform's PR history view so reviewers
    /// understand why polyphony abandoned the PR rather than merging
    /// it.</para>
    /// </summary>
    internal const string DefaultResetComment =
        "Closed by `polyphony reset root` — this PR's root is being reset for redispatch.";
}

