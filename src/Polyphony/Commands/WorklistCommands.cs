using Polyphony.Configuration;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Worklist verbs (<c>polyphony worklist ...</c>) — pure-read aggregations
/// over the plan tree and the run manifest, used to drive the parallel
/// tree-walker workflow that lands in Phase 7.
///
/// <para>All verbs in this group are read-only: no manifest mutation,
/// no remote calls. They walk the local twig cache for tree shape and
/// the on-disk run manifest for plan-PR status / generation counters.</para>
///
/// <para>As of Phase 7 PR #7 the verb composes <see cref="Sdlc.EdgeGraph"/>
/// and <see cref="Sdlc.ExecutionModeInjector"/> for wave ordering — that
/// requires the in-memory <see cref="ProcessConfig"/> for per-item facet /
/// decomposability lookup, mirroring how <c>EdgesCommands</c> takes
/// the same dependency.</para>
/// </summary>
public sealed partial class WorklistCommands(
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    private readonly IWorkItemRepository _repository = repository;
    private readonly ProcessConfig _processConfig = processConfig;
}
