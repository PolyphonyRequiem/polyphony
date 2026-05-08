namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observation of the <c>children_seeded</c> requirement kind. Produced by
/// <see cref="PlanObserver.ObserveChildrenSeededAsync"/> from the
/// <c>polyphony:planned</c> tag (or caller-supplied equivalent) on the work
/// item.
/// </summary>
/// <remarks>
/// The tag is the canonical write-once side effect of <c>plan seed-children</c>
/// (see <c>PlanCommands.SeedChildren.cs</c>). Reading the tag — instead of
/// counting children — correctly reports <see cref="Disposition.Satisfied"/>
/// for plans that legitimately seeded zero children (the "decomposable but
/// indivisible" case discussed in the closed-loop plan §3.4).
/// </remarks>
/// <param name="Disposition">
/// <list type="bullet">
///   <item><see cref="Disposition.Satisfied"/> when the tag is present.</item>
///   <item><see cref="Disposition.Needed"/> when the tag is absent or when
///     the underlying twig read failed (failures degrade to "not observed"
///     rather than throw — matches the existing detect-state posture).</item>
/// </list>
/// </param>
/// <param name="Reason">Short human-readable diagnostic.</param>
/// <param name="TagPresent">True when the planned tag was found; false on
/// absence OR on lookup failure. Callers that need to distinguish must
/// inspect the work item via twig directly.</param>
public sealed record ChildrenSeededObservation(
    string Disposition,
    string Reason,
    bool TagPresent);
