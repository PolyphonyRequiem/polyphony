using Polyphony.Journal;

namespace Polyphony.Commands;

/// <summary>
/// W8 (AB#????): shared helper for "is this twig/git tag foreign to
/// the current run lineage?" decisions made by routing-style verbs
/// (<c>polyphony state next-ready</c>, <c>polyphony branch next-impl</c>).
///
/// <para>
/// Tags like <c>polyphony:facets=...</c>, <c>polyphony:planned</c>,
/// and <c>polyphony:impl-merged-in-mg=...</c> are mutating
/// observations that earlier runs stamped on ADO work items / git
/// branches. They're NOT scoped to a run id, so a fresh polyphony
/// run sees them and (pre-W8) treats them as authoritative routing
/// input — even though the lineage that produced them has been
/// reset, abandoned, or never belonged to this checkout in the
/// first place.
/// </para>
///
/// <para>
/// The grounding rule (per the run-id-lineage epic, C2 spec §W8):
/// </para>
/// <list type="bullet">
///   <item>If the current run carries a <c>manual_*</c> lineage, the
///   launcher never stamped <c>POLYPHONY_RUN_ID</c>; we have no way
///   to ground anything, so trust the tag (status quo).</item>
///   <item>If a real journal is wired AND the current lineage has
///   recorded the action for this work item, trust the tag (we
///   produced it).</item>
///   <item>If a real journal is wired AND the current lineage has
///   NOT recorded the action for this work item but OTHER lineages
///   have rows for this work item, the tag is foreign residue —
///   refuse to trust it for routing purposes.</item>
///   <item>If a real journal is wired and the journal is empty for
///   this work item entirely, trust the tag (legacy/pre-journal
///   row, no grounding signal either way).</item>
/// </list>
///
/// <para>
/// The check is intentionally async and tolerant of transient
/// journal errors (fail-open: a stuck-run triage tool must not
/// itself be a footgun on the hottest routing paths).
/// </para>
/// </summary>
internal static class JournalLineageGrounding
{
    /// <summary>
    /// True when the journal evidence indicates the tag was stamped
    /// by a foreign lineage and should not be trusted as routing
    /// input. See class summary for the full decision matrix.
    /// </summary>
    /// <param name="journalStore">Live journal store. <c>null</c> or
    /// <see cref="NullJournalStore"/> → returns false (no grounding
    /// signal available, fall back to trusting the tag).</param>
    /// <param name="runContext">Current run context. When
    /// <see cref="RunContext.HasManualLineage"/> is true, returns
    /// false (no real lineage to ground against).</param>
    /// <param name="workItemId">The work item the tag is stamped on.</param>
    /// <param name="actionName">The journal action that, if recorded
    /// under the current lineage, demonstrates we produced this tag
    /// (e.g. <c>plan_seed_children</c> for the facets tag,
    /// <c>branch_mark_impl_merged</c> for the impl-merged-in-mg tag).</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<bool> IsTagForeignAsync(
        IJournalStore? journalStore,
        RunContext runContext,
        int workItemId,
        string actionName,
        CancellationToken ct)
    {
        if (journalStore is null or NullJournalStore) return false;
        if (runContext.HasManualLineage) return false;

        IReadOnlyList<JournalEntry> rows;
        try
        {
            rows = await journalStore.QueryAsync(
                new JournalQuery { WorkItemId = workItemId, Action = actionName },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail open. The journal is a hint, not a hard fence —
            // a transient SQLite error must not brick routing.
            return false;
        }

        if (rows.Count == 0) return false;

        var hasCurrent = false;
        var hasOther = false;
        foreach (var row in rows)
        {
            if (string.Equals(row.RunId, runContext.RunId, StringComparison.Ordinal))
                hasCurrent = true;
            else
                hasOther = true;
        }

        return !hasCurrent && hasOther;
    }
}
