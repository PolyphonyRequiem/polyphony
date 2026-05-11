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
/// GitHub's PR-author-cannot-self-review restriction. The
/// <c>polyphony:request-changes</c> form was a permanent loop trigger:
/// every poll re-read the comment as <c>changes_requested</c> until the
/// user manually deleted it. PR review threads have native resolved /
/// unresolved state so they self-clear on remediation — making them the
/// correct source of truth for the <c>changes_requested</c> gate.</para>
///
/// <para><b>Derivation order:</b></para>
/// <list type="number">
///   <item>If platform state is <c>MERGED</c> or <c>CLOSED</c>, short-circuit.</item>
///   <item>If any thread is <c>!IsResolved &amp;&amp; IsOutdated != true</c>, return <c>changes_requested</c>. Threads dominate — an unresolved thread blocks merge regardless of any APPROVED native review.</item>
///   <item>If threads exist (all resolved, or only unresolved threads are outdated), the platform's stale CHANGES_REQUESTED native review is suppressed: positive approval requires either an APPROVED native review or a magic-comment <c>polyphony:approve</c>. Otherwise <c>pending</c>.</item>
///   <item>If threads are empty, fall back to the platform's aggregated review decision.</item>
///   <item>Last resort: magic-comment <c>polyphony:approve</c> from the PR author (deprecated; surfaces a warning). <c>polyphony:request-changes</c> is no longer recognized — resolve a thread instead.</item>
/// </list>
/// </summary>
internal static class PrPollStateDerivation
{
    /// <summary>Magic-comment deprecation warning surfaced when the approve-comment fallback path fires.</summary>
    public const string MagicCommentDeprecationWarning =
        "Magic-comment vote (polyphony:approve) is deprecated — use a PR review (or resolve a review thread) instead. " +
        "polyphony:request-changes is no longer recognized; reviewers should leave a review thread to block merge.";

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
    public static string DeriveState(
        string platformState,
        string reviewDecision,
        IReadOnlyList<PrPollThread> threads,
        bool hasMagicApproveFromAuthor)
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

    /// <summary>
    /// True when the magic-comment fallback path actually contributed to the
    /// derived state (caller uses this to decide whether to attach
    /// <see cref="MagicCommentDeprecationWarning"/>). Returns false when
    /// the magic vote was overridden by a thread or by a native APPROVED
    /// decision.
    /// </summary>
    public static bool MagicApproveContributed(
        string platformState,
        string reviewDecision,
        IReadOnlyList<PrPollThread> threads,
        bool hasMagicApproveFromAuthor)
    {
        if (!hasMagicApproveFromAuthor) return false;
        if (string.Equals(platformState, "MERGED", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(platformState, "CLOSED", StringComparison.OrdinalIgnoreCase)) return false;

        var hasBlockingThread = false;
        foreach (var t in threads)
        {
            if (!t.IsResolved && t.IsOutdated != true) { hasBlockingThread = true; break; }
        }
        if (hasBlockingThread) return false;

        // Magic only contributes when the threads-or-native path didn't
        // already grant approval on its own.
        var nativeApproved = string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase);
        return !nativeApproved;
    }
}
