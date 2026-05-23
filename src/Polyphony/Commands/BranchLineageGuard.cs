using Polyphony.Journal;

namespace Polyphony.Commands;

/// <summary>
/// W10 (AB#3291) branch-side analog of <see cref="PrLineageGuard"/>.
/// Git branches carry no body or stamping, so the only grounding
/// signal is the journal: did a successful <c>branch_ensure_*</c>
/// row from a foreign lineage already claim this branch name?
///
/// <para>The bug this closes: <c>polyphony branch ensure-*</c> verbs
/// observe an existing remote/local branch and adopt it. After a
/// reset wipes the local journal but leaves the remote branch
/// (network drop, partial reset, cross-machine checkout), the new
/// lineage silently adopts the foreign branch, hides the divergent
/// SHA from the user, and corrupts the per-item review attribution
/// downstream.</para>
///
/// <para>Decision matrix (mirrors <see cref="JournalLineageGrounding"/>
/// posture):</para>
/// <list type="bullet">
///   <item>Current run id missing OR manual lineage → allow (no
///   grounding signal; refusing would brick local-dev verb usage).</item>
///   <item>Null/Null-store journal → allow (no grounding signal).</item>
///   <item>Query throws → allow (fail open; routing must not brick
///   on transient SQLite errors).</item>
///   <item>Journal has a successful <c>branch_ensure_*</c> row for
///   this target under the current run id → allow (we already own
///   this branch).</item>
///   <item>Journal has rows for this target only under other run
///   ids → refuse (foreign-owned).</item>
///   <item>Journal silent for this target → allow (legacy/pre-W11
///   branch, or first creation in this lineage).</item>
/// </list>
/// </summary>
internal static class BranchLineageGuard
{
    /// <summary>
    /// Outcome of the lineage check. <see cref="Reason"/> is populated
    /// on both allow and refuse so callers can log the corroborating
    /// signal regardless of direction.
    /// </summary>
    public sealed record Decision(bool Allowed, string Reason);

    /// <summary>
    /// Inspect the journal for prior <paramref name="journalAction"/>
    /// rows on <paramref name="journalTarget"/> and decide whether
    /// the current lineage may adopt the branch. Fails open on every
    /// "unknown" signal; only refuses when there is positive evidence
    /// of foreign ownership.
    /// </summary>
    internal static async Task<Decision> CheckAsync(
        IJournalStore? journal,
        string? currentRunId,
        bool isManualLineage,
        string journalAction,
        string journalTarget,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(currentRunId) || isManualLineage)
        {
            return new Decision(true, "Lineage check skipped: no run id or manual lineage.");
        }

        if (journal is null or NullJournalStore)
        {
            return new Decision(true, "Lineage check skipped: no journal store available.");
        }

        IReadOnlyList<JournalEntry> rows;
        try
        {
            rows = await journal.QueryAsync(
                new JournalQuery { Action = journalAction },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail open — transient journal errors must not block the
            // hot routing path. PrLineageGuard's symmetry: the journal
            // is a corroborating source, not a hard fence.
            return new Decision(true, "Lineage check skipped: journal query failed.");
        }

        var hasCurrent = false;
        var hasOther = false;
        string? firstForeignRunId = null;
        foreach (var row in rows)
        {
            if (!string.Equals(row.Target, journalTarget, StringComparison.Ordinal)) continue;
            if (row.Outcome != JournalOutcome.Success) continue;

            if (string.Equals(row.RunId, currentRunId, StringComparison.Ordinal))
            {
                hasCurrent = true;
            }
            else
            {
                hasOther = true;
                firstForeignRunId ??= row.RunId;
            }
        }

        if (hasCurrent)
        {
            return new Decision(true, "Journal has a prior ensure-row for this branch under the current run id.");
        }

        if (hasOther)
        {
            return new Decision(
                false,
                $"Branch '{journalTarget}' was previously {journalAction} by run_id='{firstForeignRunId}' which differs from current run_id='{currentRunId}'. Refusing to adopt a foreign-lineage branch.");
        }

        return new Decision(true, "Journal silent for this branch; allowing (legacy or first-time create).");
    }
}
