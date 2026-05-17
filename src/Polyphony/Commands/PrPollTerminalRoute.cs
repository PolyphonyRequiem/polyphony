namespace Polyphony.Commands;

/// <summary>
/// Deterministic 4-way classifier that converts a poll-status envelope
/// into the next workflow route for the sentiment-driven PR review
/// loop. Pure function over the platform-neutral envelope fields —
/// runs on every poll for every PR-shaped workflow (plan-level,
/// implement-merge-group, feature-pr, …).
///
/// <para>The 4 routes:</para>
/// <list type="bullet">
///   <item><c>merge_now</c> — All signals positive; no unresolved actionable threads. Workflow should invoke the merger.</item>
///   <item><c>already_merged</c> — PR is merged on the platform (someone merged through the UI). Workflow should skip the merger and continue to the next phase.</item>
///   <item><c>abort_unmerged</c> — PR ended without merging (GitHub: closed; ADO: abandoned OR any reviewer cast -10 reject). Workflow should bail out of this leg.</item>
///   <item><c>none</c> — Mixed or partial signals (e.g. +5 approved-with-suggestions, -5 waiting-for-author, no votes yet, positive vote with unresolved threads). Workflow should route to the LLM analyzer to interpret comment sentiment.</item>
/// </list>
///
/// <para>Inputs are platform-neutral; this function does NOT need to
/// know whether the source was GitHub or ADO. The normalized
/// <c>state</c> field already collapses MERGED/completed → <c>merged</c>
/// and CLOSED/abandoned → <c>closed</c>, and the
/// <see cref="PrPollReviewer.Vote"/> vocabulary is the same on both
/// platforms (only ADO ever emits the <c>rejected</c> vote since
/// GitHub has no equivalent of the -10 reject signal).</para>
/// </summary>
internal static class PrPollTerminalRoute
{
    public const string MergeNow = "merge_now";
    public const string AlreadyMerged = "already_merged";
    public const string AbortUnmerged = "abort_unmerged";
    public const string None = "none";

    /// <summary>
    /// Compute the route and a short human-readable reason. Reason is
    /// for operator logs / dashboard display — workflow conditions
    /// should always switch on <see cref="PrPollTerminalRouteResult.Route"/>.
    /// </summary>
    public static PrPollTerminalRouteResult Classify(
        string state,
        IReadOnlyList<PrPollReviewer> reviewers,
        IReadOnlyList<PrPollThread> threads)
    {
        if (string.Equals(state, "merged", StringComparison.OrdinalIgnoreCase))
            return new(AlreadyMerged, "PR is merged on the platform");

        if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
            return new(AbortUnmerged, "PR was closed without being merged");

        // ADO -10 reject: ends the leg regardless of any positive vote
        // that may co-exist. Locked by design (operator restarts the run
        // manually if they disagree with the reject).
        foreach (var r in reviewers)
        {
            if (string.Equals(r.Vote, "rejected", StringComparison.OrdinalIgnoreCase))
                return new(AbortUnmerged, $"reviewer '{r.Identity}' cast a rejecting vote");
        }

        // Mergeable state: approved + no unresolved actionable threads.
        // PrPollStateDerivation.DeriveState already returns 'pending' or
        // 'changes_requested' when threads block, so state == 'approved'
        // here implies no thread blockers — but be explicit so future
        // refactors of DeriveState don't accidentally unlock merge.
        if (string.Equals(state, "approved", StringComparison.OrdinalIgnoreCase)
            && !HasBlockingThread(threads))
        {
            return new(MergeNow, "PR is approved with no unresolved actionable threads");
        }

        return new(None, $"no terminal signal (state='{state}'); defer to sentiment analyzer");
    }

    private static bool HasBlockingThread(IReadOnlyList<PrPollThread> threads)
    {
        foreach (var t in threads)
        {
            if (!t.IsResolved && t.IsOutdated != true) return true;
        }
        return false;
    }
}

/// <summary>Output of <see cref="PrPollTerminalRoute.Classify"/>.</summary>
internal readonly record struct PrPollTerminalRouteResult(string Route, string Reason);
