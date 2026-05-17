namespace Polyphony.Commands;

/// <summary>
/// Single derivation rule for <see cref="PrPollStatusResult.State"/>.
/// Shared between the GitHub leg (<see cref="PrCommands.PollStatus"/>) and
/// the ADO leg (<see cref="PrCommands.PollStatusAdo"/>) so the two
/// platforms can never drift on the routing vocabulary.
///
/// <para><b>Background — issue #207, "option B".</b> The original
/// implementation used "magic comments" (<c>polyphony:approve</c> /
/// <c>polyphony:request-changes</c> in PR-author comments) to work around
/// GitHub's PR-author-cannot-self-review restriction. The bare
/// <c>polyphony:request-changes</c> form was retired because it was a
/// permanent loop trigger: every poll re-read the comment as
/// <c>changes_requested</c> until the user manually deleted it. PR review
/// threads have native resolved / unresolved state so they self-clear on
/// remediation — making them the correct source of truth for the
/// <c>changes_requested</c> gate <em>for non-author reviewers</em>.</para>
///
/// <para><b>Background — SHA-bound magic approve.</b> The bare
/// <c>polyphony:approve</c> magic comment had the same loop shape as the
/// retired bare <c>polyphony:request-changes</c>: an approval comment
/// posted against commit X would still match (and re-approve) commit Y
/// after a force-push or new commit. The canonical form is now
/// <c>polyphony:approve &lt;head-sha&gt;</c>: the SHA pins the approval to a
/// specific commit, so any new commit silently invalidates the old
/// comment without any timestamp-comparison logic. The bare form is still
/// recognized as a deprecation fallback (with a warning that includes the
/// current head SHA so the user can copy-paste the canonical form).</para>
///
/// <para><b>Background — SHA-bound magic request-changes resurrection.</b>
/// GitHub PR authors cannot leave a <c>REQUEST_CHANGES</c> review on
/// their own PR, and the review-thread escape hatch only works for
/// non-author reviewers (an author can open a thread but cannot block
/// merge with it). The author has no native way to pause merge of their
/// own PR. The SHA-bound <c>polyphony:request-changes &lt;head-sha&gt;</c>
/// form fills that gap. SHA is mandatory (no bare-form fallback): a new
/// commit silently invalidates the block, so a remediation push
/// auto-clears it — the structural property that prevents the
/// permanent-loop pathology that retired the bare form.</para>
///
/// <para><b>Derivation order:</b></para>
/// <list type="number">
///   <item>If platform state is <c>MERGED</c> or <c>CLOSED</c>, short-circuit.</item>
///   <item>If any thread is <c>!IsResolved &amp;&amp; IsOutdated != true</c>, return <c>changes_requested</c>. Threads dominate — an unresolved thread blocks merge regardless of any APPROVED native review.</item>
///   <item>If the author's most-recent magic vote is <c>polyphony:request-changes &lt;sha&gt;</c> (SHA matches head, more recent than any author native review or magic-approve), return <c>changes_requested</c>. The author self-block path.</item>
///   <item>If threads exist (all resolved, or only unresolved threads are outdated), the platform's stale CHANGES_REQUESTED native review is suppressed: positive approval requires either an APPROVED native review or a magic-comment <c>polyphony:approve</c>. Otherwise <c>pending</c>.</item>
///   <item>If threads are empty, fall back to the platform's aggregated review decision.</item>
///   <item>Last resort: magic-comment <c>polyphony:approve [head-sha]</c> from the PR author. The SHA-bound form is canonical (no warning); the bare form is recognized but emits a deprecation warning.</item>
/// </list>
/// </summary>
internal static class PrPollStateDerivation
{
    /// <summary>Pagination warning surfaced when GitHub's reviewThreads connection has more pages than the verb fetched.</summary>
    public const string ThreadPaginationWarning =
        "PR has more than 100 review threads; only the first 100 were considered. " +
        "If a blocking thread exists on a later page, polyphony cannot see it — failing closed.";

    /// <summary>
    /// Derive the platform-neutral state field. Inputs are already
    /// normalised by the caller (raw platform state in upper-case;
    /// review decision in upper-case; thread list filtered to
    /// human-authored / non-tombstoned).
    /// </summary>
    /// <param name="platformState">Upper-case platform state: <c>MERGED | CLOSED | OPEN</c>.</param>
    /// <param name="reviewDecision">Upper-case aggregated review decision: <c>APPROVED | CHANGES_REQUESTED | REVIEW_REQUIRED | REJECTED | (empty)</c>.</param>
    /// <param name="threads">All visible review threads.</param>
    /// <param name="hasMagicApproveFromAuthor">True when the PR author posted a <c>polyphony:approve</c> top-level comment more recent than any native review the author left.</param>
    /// <param name="hasMagicChangesRequestedFromAuthor">True when the PR author posted a SHA-matching <c>polyphony:request-changes</c> top-level comment more recent than any other author vote (native review, magic-approve).</param>
    public static string DeriveState(
        string platformState,
        string reviewDecision,
        IReadOnlyList<PrPollThread> threads,
        bool hasMagicApproveFromAuthor,
        bool hasMagicChangesRequestedFromAuthor = false)
    {
        if (string.Equals(platformState, "MERGED", StringComparison.OrdinalIgnoreCase)) return "merged";
        if (string.Equals(platformState, "CLOSED", StringComparison.OrdinalIgnoreCase)) return "closed";

        var hasBlockingThread = false;
        var hasAnyVisibleThread = threads.Count > 0;
        foreach (var t in threads)
        {
            if (!t.IsResolved && t.IsOutdated != true)
            {
                hasBlockingThread = true;
                break;
            }
        }

        if (hasBlockingThread) return "changes_requested";

        // Author self-block: SHA-bound magic-request-changes from the
        // author dominates approval signals (their explicit pause). Does
        // NOT dominate threads — a non-author reviewer's blocking thread
        // is a third-party concern that should still surface even if the
        // author has also paused.
        if (hasMagicChangesRequestedFromAuthor) return "changes_requested";

        if (hasAnyVisibleThread)
        {
            // Threads exist and none block. The stale native CHANGES_REQUESTED
            // signal does NOT propagate — that's the loop fix.
            if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)) return "approved";
            if (hasMagicApproveFromAuthor) return "approved";
            return "pending";
        }

        // No threads — full fallback to native decision, then magic comment.
        if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)) return "changes_requested";
        if (string.Equals(reviewDecision, "REJECTED", StringComparison.OrdinalIgnoreCase)) return "changes_requested";
        if (hasMagicApproveFromAuthor) return "approved";
        return "pending";
    }
}
