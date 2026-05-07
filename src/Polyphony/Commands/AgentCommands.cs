using Polyphony.Configuration;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Agent verbs (<c>polyphony agent ...</c>) — the driver-side helpers a
/// workflow shells out to in order to compose the context an agent step
/// will run with. Phase 6 PR #5 ships <c>agent compose-addendum</c>; the
/// only verb in this group today.
///
/// <para>The verbs in this group are pure-read: they walk the local twig
/// cache for the work item, the in-memory <see cref="ProcessConfig"/> for
/// facet bindings, and the resolved policy for guidance — no manifest
/// mutation, no remote calls. They emit JSON envelopes the actionable
/// (and, eventually, plannable / implementable) workflow consumes via
/// Jinja2 prompt-text injection.</para>
/// </summary>
public sealed partial class AgentCommands(
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    private readonly IWorkItemRepository _repository = repository;
    private readonly ProcessConfig _processConfig = processConfig;
}
