using Polyphony.Journal;

namespace Polyphony.Commands;

/// <summary>
/// W9 (AB#3282): pre-merge "is this PR ours?" check. The merge verbs
/// (<c>pr merge-plan-pr</c>, <c>merge-impl-pr</c>, <c>merge-mg-pr</c>,
/// <c>merge-evidence-pr</c>) currently merge whatever PR number
/// resolves on the expected branch without checking whether *this*
/// lineage opened it. That is the irreversibility firewall this
/// helper closes: a foreign PR (operator-opened, leftover-from-prior-
/// run, opened by a parallel manual lineage) must NOT be merged by
/// an automated driver.
///
/// <para>Two corroborating sources of "ours-ness", in order:</para>
/// <list type="number">
///   <item>Body marker. Non-plan PRs carry a hidden
///   <c>polyphony:run_id=...</c> HTML comment (see
///   <see cref="PrBodyMarker"/>); plan PRs carry the same value in
///   their YAML front-matter <c>run_id</c> key. The body-side check
///   is the cross-machine ground truth — it survives even when the
///   local journal is empty (fresh checkout, machine swap, lost
///   tmpdir).</item>
///   <item>Journal. A <c>pr_open_*</c> action row with matching
///   <c>run_id</c> and target proves this lineage opened the PR.
///   Used as the fallback when the marker has been stripped by an
///   operator edit OR the PR pre-dates the W6/W7 stamping rollout.
///   Skipped silently when the journal store is the null store or
///   the current lineage is a <c>manual_*</c> fallback — neither
///   case can distinguish "we didn't open it" from "we have no idea".</item>
/// </list>
///
/// <para>Posture: refuse on mismatch. Returning a structured
/// <see cref="Decision"/> rather than throwing keeps the callers'
/// routing-style envelope semantics intact (the verb emits an error
/// envelope and exits 0 with a non-success error code).</para>
/// </summary>
internal static class PrLineageGuard
{
    /// <summary>
    /// Outcome of the lineage check. Allow-cases differ only in
    /// observability (which source corroborated the decision); refuse
    /// carries a structured reason for the verb's error envelope.
    /// </summary>
    public sealed record Decision(bool Allowed, string Reason, string Source);

    /// <summary>
    /// Inspect <paramref name="body"/> for a body-side stamp (marker
    /// or plan-PR front-matter), optionally back-stop with the
    /// journal, and decide whether this PR belongs to
    /// <paramref name="currentRunId"/>. <paramref name="bodyHasFrontMatter"/>
    /// distinguishes plan-PR bodies (parse front-matter) from
    /// non-plan PR bodies (parse <see cref="PrBodyMarker"/>).
    /// </summary>
    public static async Task<Decision> CheckAsync(
        string? body,
        string? currentRunId,
        bool isManualLineage,
        bool bodyHasFrontMatter,
        IJournalStore journal,
        string journalAction,
        string journalTarget,
        CancellationToken ct)
    {
        // Manual lineage can't ground anything — fall through to allow.
        // Same posture as DetectState's W3 short-circuit: a manual_*
        // run id is "we don't actually know who we are", and refusing
        // every merge under it would brick local-dev verb usage.
        if (string.IsNullOrEmpty(currentRunId) || isManualLineage)
        {
            return new Decision(true, "Lineage check skipped: no run id or manual lineage.", "skipped");
        }

        var bodyRunId = bodyHasFrontMatter
            ? PlanPrFrontMatter.Parse(body ?? string.Empty).RunId
            : PrBodyMarker.TryParseRunId(body);

        if (string.Equals(bodyRunId, currentRunId, StringComparison.Ordinal))
        {
            return new Decision(true, "Body stamp matches current run id.", "body");
        }

        if (!string.IsNullOrEmpty(bodyRunId))
        {
            // Stamp exists and is foreign: refuse without consulting the
            // journal. A mismatched body stamp is a stronger signal than
            // journal silence — someone else owns this PR.
            return new Decision(
                false,
                $"PR body carries run_id='{bodyRunId}' which differs from current run_id='{currentRunId}'. Refusing to merge a foreign-lineage PR.",
                "body");
        }

        // No body stamp. Fall back to the journal: did THIS lineage
        // open this PR? A NullJournalStore can't answer; treat that
        // as "we don't know" and allow (no regression from pre-W9
        // behaviour for unstamped legacy PRs in null-store envs).
        if (journal is NullJournalStore)
        {
            return new Decision(true, "Body stamp absent and journal store is null; lineage check inconclusive, allowing.", "inconclusive");
        }

        var rows = await journal.QueryAsync(
            new JournalQuery
            {
                RunId = currentRunId,
                Action = journalAction,
            },
            ct).ConfigureAwait(false);
        var ours = rows.Any(r => string.Equals(r.Target, journalTarget, StringComparison.Ordinal)
            && r.Outcome == JournalOutcome.Success);
        if (ours)
        {
            return new Decision(true, "Journal has a successful open-row for this PR target under the current run id.", "journal");
        }

        return new Decision(
            false,
            $"No body stamp and no journal evidence that run_id='{currentRunId}' opened a PR with target='{journalTarget}'. Refusing to merge a foreign-lineage PR.",
            "journal");
    }
}
