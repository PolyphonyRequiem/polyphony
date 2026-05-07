namespace Polyphony.Sdlc;

/// <summary>
/// Pure reducer that overlays observed dispositions onto a derived
/// <see cref="RequirementSet"/> and then promotes <see cref="Disposition.Needed"/>
/// requirements to <see cref="Disposition.Ready"/> when their prerequisite edges
/// are satisfied.
/// </summary>
/// <remarks>
/// <para>
/// Two-phase algorithm:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Overlay</b>: each requirement's disposition is replaced by the observed
///     disposition for its kind, defaulting to <see cref="Disposition.Needed"/>
///     when the observer says nothing.
///   </description></item>
///   <item><description>
///     <b>Fixpoint promotion</b>: any requirement still at <see cref="Disposition.Needed"/>
///     whose incoming prerequisite edges all meet their thresholds (per
///     <see cref="Disposition.Meets"/>) is promoted to <see cref="Disposition.Ready"/>.
///     This loops to a fixpoint; the only transition is monotonic (Needed → Ready),
///     so termination is bounded by the requirement count.
///   </description></item>
/// </list>
/// <para>
/// The reducer never downgrades observed <see cref="Disposition.Fulfilling"/> or
/// <see cref="Disposition.Satisfied"/> — those are authoritative. It also never
/// emits <see cref="Disposition.Ready"/> via observation; that disposition is
/// reducer-computed only.
/// </para>
/// </remarks>
public static class RequirementSetReducer
{
    /// <summary>
    /// Apply <paramref name="observed"/> to <paramref name="set"/> and return a
    /// new set with computed dispositions. Throws on invalid observation
    /// (use <see cref="ObservedRequirementState.Validate"/> first if accepting
    /// untrusted input).
    /// </summary>
    public static RequirementSet Apply(RequirementSet set, ObservedRequirementState observed)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(observed);

        // Phase 1: overlay observed dispositions.
        var items = set.Items
            .Select(r =>
            {
                var obs = observed.GetOrNeeded(r.Kind);
                if (obs == Disposition.Ready)
                {
                    // Defensive: callers should validate first. Treat as needed
                    // (will be reducer-promoted if prerequisites are met).
                    obs = Disposition.Needed;
                }
                return r with { Disposition = obs };
            })
            .ToList();

        // Index by kind for O(1) prerequisite lookup. The deriver does not
        // currently emit duplicate kinds, but we tolerate the first one.
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            byKind.TryAdd(items[i].Kind, i);
        }

        // Pre-group edges by dependent kind for the fixpoint loop.
        var edgesByDependent = set.Edges
            .GroupBy(e => e.DependentKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Phase 2: fixpoint Needed → Ready/Satisfied promotion.
        //
        // ItemSatisfied is a synthetic terminal — it is never observed. Its
        // disposition is derived purely from its incoming edges:
        //   * If it has no incoming within-item edges (pure container case),
        //     it stays Needed; only cross-item rollup from children can move
        //     it (out of scope for PR #1).
        //   * If all incoming edges are met, it promotes straight to Satisfied
        //     (skipping Ready) — there is nothing to dispatch, the item is
        //     wholly done.
        // All other requirements use the standard Needed → Ready promotion.
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < items.Count; i++)
            {
                var req = items[i];
                if (req.Disposition != Disposition.Needed) continue;

                var isTerminal = string.Equals(
                    req.Kind, RequirementKind.ItemSatisfied, StringComparison.Ordinal);

                if (!edgesByDependent.TryGetValue(req.Kind, out var prereqEdges))
                {
                    if (isTerminal)
                    {
                        // Pure container: no leaves to roll up. Wait for
                        // cross-item rollup (future). Stay Needed.
                        continue;
                    }
                    // Standard requirement with no prerequisites — promote.
                    items[i] = req with { Disposition = Disposition.Ready };
                    changed = true;
                    continue;
                }

                var allMet = true;
                foreach (var edge in prereqEdges)
                {
                    if (!byKind.TryGetValue(edge.PrerequisiteKind, out var prereqIdx))
                    {
                        // Edge references a kind not in this set — treat as unmet.
                        // The deriver normally guarantees consistency; this is a
                        // safety net for hand-built sets.
                        allMet = false;
                        break;
                    }
                    var prereqDisp = items[prereqIdx].Disposition;
                    if (!Disposition.Meets(prereqDisp, edge.RequiredDisposition))
                    {
                        allMet = false;
                        break;
                    }
                }

                if (allMet)
                {
                    var promoted = isTerminal ? Disposition.Satisfied : Disposition.Ready;
                    items[i] = req with { Disposition = promoted };
                    changed = true;
                }
            }
        } while (changed);

        return new RequirementSet(items, set.Edges);
    }
}
