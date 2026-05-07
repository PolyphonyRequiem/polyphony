using Polyphony.Configuration;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Edges verbs (<c>polyphony edges ...</c>) — pure-read aggregations over
/// the plan tree that build a cross-item <see cref="Sdlc.EdgeGraph"/>
/// and surface its diagnostics. PR #3 of the Phase 7 edges arc ships
/// <c>edges check</c>; the apex driver in the worklist retrofit consumes
/// the JSON envelope to gate dispatch.
///
/// <para>All verbs in this group are read-only: no manifest mutation,
/// no remote calls. They walk the local twig cache for tree shape and
/// the in-memory <see cref="ProcessConfig"/> for facets / decomposability.</para>
/// </summary>
public sealed partial class EdgesCommands(
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    private readonly IWorkItemRepository _repository = repository;
    private readonly ProcessConfig _processConfig = processConfig;
}
