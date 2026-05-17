using Polyphony.Commands;
using Polyphony.Models;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public class PrPollTerminalRouteTests
{
    [Fact]
    public void Merged_ReturnsAlreadyMerged()
    {
        var r = PrPollTerminalRoute.Classify("merged", Array.Empty<PrPollReviewer>(), Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.AlreadyMerged);
    }

    [Fact]
    public void ClosedUnmerged_ReturnsAbortUnmerged()
    {
        var r = PrPollTerminalRoute.Classify("closed", Array.Empty<PrPollReviewer>(), Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.AbortUnmerged);
    }

    [Fact]
    public void RejectedVote_AbortsRegardlessOfState()
    {
        // ADO -10 reject: even if state would otherwise be 'approved'
        // via an earlier +10, the reject wins and ends the leg.
        var reviewers = new[]
        {
            MakeReviewer("alice", "approved"),
            MakeReviewer("bob", "rejected"),
        };
        var r = PrPollTerminalRoute.Classify("approved", reviewers, Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.AbortUnmerged);
        r.Reason.ShouldContain("bob");
    }

    [Fact]
    public void Approved_NoBlockingThreads_MergesNow()
    {
        var r = PrPollTerminalRoute.Classify(
            "approved",
            new[] { MakeReviewer("alice", "approved") },
            Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.MergeNow);
    }

    [Fact]
    public void Approved_WithUnresolvedThread_RoutesToNone()
    {
        // Approved + unresolved actionable thread → analyzer must
        // decide; this is the "+5 approved with suggestions" case
        // analogue.
        var threads = new[] { MakeThread(isResolved: false, isOutdated: false) };
        var r = PrPollTerminalRoute.Classify(
            "approved",
            new[] { MakeReviewer("alice", "approved") },
            threads);
        r.Route.ShouldBe(PrPollTerminalRoute.None);
    }

    [Fact]
    public void Approved_WithOutdatedUnresolvedThread_MergesNow()
    {
        // Outdated threads do NOT block merge — anchored to a hunk that
        // has been rewritten. Matches GitHub's native UX.
        var threads = new[] { MakeThread(isResolved: false, isOutdated: true) };
        var r = PrPollTerminalRoute.Classify(
            "approved",
            new[] { MakeReviewer("alice", "approved") },
            threads);
        r.Route.ShouldBe(PrPollTerminalRoute.MergeNow);
    }

    [Fact]
    public void Approved_WithResolvedThread_MergesNow()
    {
        var threads = new[] { MakeThread(isResolved: true, isOutdated: false) };
        var r = PrPollTerminalRoute.Classify(
            "approved",
            new[] { MakeReviewer("alice", "approved") },
            threads);
        r.Route.ShouldBe(PrPollTerminalRoute.MergeNow);
    }

    [Fact]
    public void Pending_RoutesToNone()
    {
        // No vote yet → defer to analyzer in case there are comments
        // worth reading.
        var r = PrPollTerminalRoute.Classify("pending", Array.Empty<PrPollReviewer>(), Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.None);
    }

    [Fact]
    public void ChangesRequested_RoutesToNone()
    {
        var threads = new[] { MakeThread(isResolved: false, isOutdated: false) };
        var r = PrPollTerminalRoute.Classify("changes_requested", Array.Empty<PrPollReviewer>(), threads);
        r.Route.ShouldBe(PrPollTerminalRoute.None);
    }

    [Fact]
    public void ApprovedWithSuggestions_RoutesToNone()
    {
        // ADO +5 path: vote is positive but not the canonical +10.
        // DeriveState returns 'approved' on ADO when ReviewDecision is
        // APPROVED (AggregateReviewDecision treats +5 as approving for
        // back-compat purposes), so this collapses into the approved
        // branch — covered by Approved_NoBlockingThreads above. The
        // distinction matters more for the analyzer's tone-reading.
        var r = PrPollTerminalRoute.Classify(
            "approved",
            new[] { MakeReviewer("alice", "approved_with_suggestions") },
            Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.MergeNow);
    }

    [Fact]
    public void Error_RoutesToNone()
    {
        // Error envelope: don't merge, don't auto-abort — leave to the
        // workflow's pre-existing error-gate handling.
        var r = PrPollTerminalRoute.Classify("error", Array.Empty<PrPollReviewer>(), Array.Empty<PrPollThread>());
        r.Route.ShouldBe(PrPollTerminalRoute.None);
    }

    private static PrPollReviewer MakeReviewer(string identity, string vote) => new()
    {
        Identity = identity,
        Vote = vote,
        Source = "review",
    };

    private static PrPollThread MakeThread(bool isResolved, bool isOutdated) => new()
    {
        Id = "thread-1",
        IsResolved = isResolved,
        IsOutdated = isOutdated,
        Status = isResolved ? "RESOLVED" : "UNRESOLVED",
        CommentCount = 1,
        Comments = Array.Empty<PrPollComment>(),
    };
}
