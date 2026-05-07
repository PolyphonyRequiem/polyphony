namespace Polyphony.Sdlc;

/// <summary>
/// The composed addendum the driver injects into an agent invocation: union
/// of all <see cref="FacetProfile"/>s for the item's facet set, plus any
/// per-item guidance carried as append-only prompt context.
/// </summary>
/// <remarks>
/// <para>
/// Produced by <see cref="FacetProfileComposer.Compose"/> at agent-invocation
/// prep time. <see cref="Skills"/> and <see cref="Mcps"/> are deduped and
/// sorted ascending — determinism matters for snapshot tests and reviewers.
/// </para>
/// <para>
/// <see cref="GuidanceContext"/> is passed through verbatim from whatever the
/// caller supplied for the item (extracted from a description fenced block
/// or an opt-in dedicated field — see PR #6 of Phase 6). It is never
/// composed, never merged, never order-sensitive.
/// </para>
/// </remarks>
public sealed record AgentAddendum(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Mcps,
    string? GuidanceContext);
