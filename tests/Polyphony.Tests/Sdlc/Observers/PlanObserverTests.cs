using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc.Observers;

/// <summary>
/// Tests for <see cref="PlanObserver"/>: each observation method is exercised
/// against a happy path, a missing-PR path, and a transient-error path.
/// Every observation must populate a non-empty <c>Reason</c>.
/// </summary>
public sealed class PlanObserverTests
{
    private const int RootId = 100;
    private const int ChildId = 200;
    private const string ChildPlanBranch = "plan/100-200";
    private const string RootPlanBranch = "plan/100";

    private static PlanObserver CreateObserver(FakeProcessRunner runner)
    {
        var git = new GitClient(runner);
        return new PlanObserver(git, new GhClient(runner), new ThrowingAdoClient(), new TwigClient(runner), new RepoIdentityResolver(git));
    }

    private static void StubRemoteUrl(FakeProcessRunner runner, string url = "https://github.com/acme/repo.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubRemoteUrlMissing(FakeProcessRunner runner)
        => runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: No such remote 'origin'"));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListSingle(FakeProcessRunner runner, int number, string headRef, string url = "https://gh/pr/1")
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0,
                $$"""[{"number":{{number}},"headRefName":"{{headRef}}","url":"{{url}}"}]""",
                ""));

    private static void StubPrListError(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(1, "", "boom"));

    private static void StubPrPoll(
        FakeProcessRunner runner,
        int prNumber,
        string state,
        string reviewDecision = "REVIEW_REQUIRED")
    {
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "{{reviewDecision}}",
              "mergeable": "MERGEABLE",
              "headRefName": "{{ChildPlanBranch}}",
              "headRefOid": "abc123",
              "baseRefName": "feature/100",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubTwigShowWithTags(FakeProcessRunner runner, int itemId, string tags)
        => runner.WhenExact("twig", ["show", itemId.ToString(), "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{itemId}},"title":"Item","tags":"{{tags}}"}""", ""));

    private static void StubTwigShowError(FakeProcessRunner runner, int itemId)
        => runner.WhenExact("twig", ["show", itemId.ToString(), "--output", "json"],
            new ProcessResult(1, "", "not found"));

    // ─── ResolvePlanBranch (pure helper) ──────────────────────────────────

    [Fact]
    public void ResolvePlanBranch_RootEqualsItem_ReturnsRootForm()
        => PlanObserver.ResolvePlanBranch(RootId, RootId).ShouldBe(RootPlanBranch);

    [Fact]
    public void ResolvePlanBranch_DescendantItem_ReturnsHyphenForm()
        => PlanObserver.ResolvePlanBranch(RootId, ChildId).ShouldBe(ChildPlanBranch);

    [Fact]
    public void ResolvePlanBranch_InvalidRoot_ReturnsEmpty()
        => PlanObserver.ResolvePlanBranch(0, ChildId).ShouldBeEmpty();

    // ─── ObservePlanAuthoredAsync ─────────────────────────────────────────

    [Fact]
    public async Task ObservePlanAuthored_NoBranchNoPr_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        StubPrListEmpty(runner);

        var obs = await CreateObserver(runner).ObservePlanAuthoredAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PlanBranch.ShouldBe(ChildPlanBranch);
        obs.BranchExistsOnOrigin.ShouldBeFalse();
        obs.PrNumber.ShouldBeNull();
    }

    [Fact]
    public async Task ObservePlanAuthored_OpenPr_FulfillingWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch, "https://gh/pr/42");
        StubPrPoll(runner, 42, "OPEN");

        var obs = await CreateObserver(runner).ObservePlanAuthoredAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Fulfilling);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrNumber.ShouldBe(42);
        obs.PrUrl.ShouldBe("https://gh/pr/42");
        obs.PrState.ShouldBe("OPEN");
        obs.BranchExistsOnOrigin.ShouldBeTrue();
    }

    [Fact]
    public async Task ObservePlanAuthored_MergedPr_SatisfiedWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");

        var obs = await CreateObserver(runner).ObservePlanAuthoredAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("MERGED");
    }

    [Fact]
    public async Task ObservePlanAuthored_ClosedUnmergedPr_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "CLOSED");

        var obs = await CreateObserver(runner).ObservePlanAuthoredAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("CLOSED");
    }

    [Fact]
    public async Task ObservePlanAuthored_NoOriginRemote_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrlMissing(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);

        var obs = await CreateObserver(runner).ObservePlanAuthoredAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.Reason.ShouldContain("repo identity");
        obs.PrNumber.ShouldBeNull();
    }

    // ─── ObservePlanReviewedAsync ─────────────────────────────────────────

    [Fact]
    public async Task ObservePlanReviewed_OpenApprovedPr_SatisfiedWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch, "https://gh/pr/42");
        StubPrPoll(runner, 42, "OPEN", reviewDecision: "APPROVED");

        var obs = await CreateObserver(runner).ObservePlanReviewedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.ReviewDecision.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task ObservePlanReviewed_OpenPendingReview_FulfillingWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "OPEN", reviewDecision: "REVIEW_REQUIRED");

        var obs = await CreateObserver(runner).ObservePlanReviewedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Fulfilling);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("OPEN");
    }

    [Fact]
    public async Task ObservePlanReviewed_MergedPr_SatisfiedImpliedApproval()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "MERGED");

        var obs = await CreateObserver(runner).ObservePlanReviewedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("MERGED");
    }

    [Fact]
    public async Task ObservePlanReviewed_NoPr_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListEmpty(runner);

        var obs = await CreateObserver(runner).ObservePlanReviewedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrNumber.ShouldBeNull();
    }

    [Fact]
    public async Task ObservePlanReviewed_PrListError_NeededWithReason()
    {
        // gh pr list non-zero exit is treated as "no PRs" by GhClient
        // (see ListPullRequestsAsync). The observer therefore degrades to
        // "no observation" with a Needed disposition.
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListError(runner);

        var obs = await CreateObserver(runner).ObservePlanReviewedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrNumber.ShouldBeNull();
    }

    // ─── ObservePlanPromotedAsync ─────────────────────────────────────────

    [Fact]
    public async Task ObservePlanPromoted_MergedPr_SatisfiedWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch, "https://gh/pr/42");
        StubPrPoll(runner, 42, "MERGED");

        var obs = await CreateObserver(runner).ObservePlanPromotedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("MERGED");
    }

    [Fact]
    public async Task ObservePlanPromoted_OpenPr_FulfillingWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "OPEN");

        var obs = await CreateObserver(runner).ObservePlanPromotedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Fulfilling);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ObservePlanPromoted_ClosedUnmergedPr_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListSingle(runner, 42, ChildPlanBranch);
        StubPrPoll(runner, 42, "CLOSED");

        var obs = await CreateObserver(runner).ObservePlanPromotedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrState.ShouldBe("CLOSED");
    }

    [Fact]
    public async Task ObservePlanPromoted_NoPr_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubRemoteUrl(runner);
        StubPrListEmpty(runner);

        var obs = await CreateObserver(runner).ObservePlanPromotedAsync(RootId, ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.PrNumber.ShouldBeNull();
    }

    // ─── ObserveChildrenSeededAsync ───────────────────────────────────────

    [Fact]
    public async Task ObserveChildrenSeeded_TagPresent_SatisfiedWithReason()
    {
        var runner = new FakeProcessRunner();
        StubTwigShowWithTags(runner, ChildId, "polyphony;polyphony:planned");

        var obs = await CreateObserver(runner).ObserveChildrenSeededAsync(ChildId);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.TagPresent.ShouldBeTrue();
    }

    [Fact]
    public async Task ObserveChildrenSeeded_TagAbsent_NeededWithReason()
    {
        var runner = new FakeProcessRunner();
        StubTwigShowWithTags(runner, ChildId, "polyphony;something:else");

        var obs = await CreateObserver(runner).ObserveChildrenSeededAsync(ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.TagPresent.ShouldBeFalse();
    }

    [Fact]
    public async Task ObserveChildrenSeeded_TwigShowError_NeededWithReason()
    {
        // twig show failure degrades to "not seeded" — the safe default that
        // reschedules the seeder downstream.
        var runner = new FakeProcessRunner();
        StubTwigShowError(runner, ChildId);

        var obs = await CreateObserver(runner).ObserveChildrenSeededAsync(ChildId);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldNotBeNullOrWhiteSpace();
        obs.TagPresent.ShouldBeFalse();
    }

    [Fact]
    public async Task ObserveChildrenSeeded_CustomPlannedTag_HonoursOverride()
    {
        var runner = new FakeProcessRunner();
        StubTwigShowWithTags(runner, ChildId, "custom:tag-name");

        var obs = await CreateObserver(runner).ObserveChildrenSeededAsync(ChildId, plannedTag: "custom:tag-name");

        obs.Disposition.ShouldBe(Disposition.Satisfied);
        obs.TagPresent.ShouldBeTrue();
    }

    // ─── ReadRunStartedAtAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReadRunStartedAt_TagPresent_ReturnsParsedInstant()
    {
        var runner = new FakeProcessRunner();
        StubTwigShowWithTags(runner, RootId, "polyphony;polyphony:root;polyphony:run-started-at=2026-05-17T22:00:00Z");

        var instant = await CreateObserver(runner).ReadRunStartedAtAsync(RootId);

        instant.ShouldNotBeNull();
        instant!.Value.UtcDateTime.ShouldBe(new DateTime(2026, 5, 17, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ReadRunStartedAt_TagAbsent_ReturnsNull()
    {
        var runner = new FakeProcessRunner();
        StubTwigShowWithTags(runner, RootId, "polyphony;polyphony:root");

        var instant = await CreateObserver(runner).ReadRunStartedAtAsync(RootId);

        instant.ShouldBeNull();
    }

    [Fact]
    public async Task ReadRunStartedAt_TwigShowFails_PropagatesException()
    {
        // BLOCKER #3 fix: the reader MUST NOT swallow twig failures. A
        // failed twig show is operationally distinct from an absent tag,
        // and callers (FetchRunStartedAtAsync in next-ready,
        // DetectState) need to distinguish them to force a Needed
        // disposition rather than silently falling back to no-filter on
        // a reset apex. TwigClient.ShowAsync itself returns null on
        // process failure; the observer translates that into a thrown
        // InvalidOperationException so the caller's catch fires.
        // See docs/decisions/run-reset.md.
        var runner = new FakeProcessRunner();
        StubTwigShowError(runner, RootId);

        var observer = CreateObserver(runner);

        var act = async () => await observer.ReadRunStartedAtAsync(RootId);
        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    // ─── Map* run-watermark filter ────────────────────────────────────────

    [Fact]
    public void MapPlanAuthored_MergedBeforeFilter_DowngradesToNeeded()
    {
        var pr = new PullRequestSummary(7, ChildPlanBranch, "https://gh/pr/7", MergedAt: null);
        var mergedAt = DateTimeOffset.Parse("2026-05-10T12:00:00Z");
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z"); // newer

        var obs = PlanObserver.MapPlanAuthored(
            ChildPlanBranch, branchExists: true, pr, prState: "MERGED",
            mergedAt: mergedAt, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldContain("prior run");
        obs.PrNumber.ShouldBe(7); // surfaced for diagnostics
        obs.PrState.ShouldBe("MERGED");
    }

    [Fact]
    public void MapPlanAuthored_MergedAfterFilter_StaysSatisfied()
    {
        var pr = new PullRequestSummary(7, ChildPlanBranch, "https://gh/pr/7", MergedAt: null);
        var mergedAt = DateTimeOffset.Parse("2026-05-20T12:00:00Z");
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z"); // older

        var obs = PlanObserver.MapPlanAuthored(
            ChildPlanBranch, branchExists: true, pr, prState: "MERGED",
            mergedAt: mergedAt, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
    }

    [Fact]
    public void MapPlanAuthored_FilterAbsent_LegacyBehaviorPreserved()
    {
        var pr = new PullRequestSummary(7, ChildPlanBranch, "https://gh/pr/7", MergedAt: null);

        var obs = PlanObserver.MapPlanAuthored(
            ChildPlanBranch, branchExists: true, pr, prState: "MERGED",
            mergedAt: DateTimeOffset.Parse("2026-05-10T12:00:00Z"),
            runStartedAtFilter: null);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
    }

    [Fact]
    public void MapPlanAuthored_OpenPrWithFilter_NotFiltered()
    {
        // Open PRs are NOT filtered by the watermark — reset abandons them
        // explicitly; if an open PR survives reset that's reset's bug, not
        // the observer's. Verifies that filter has no effect on OPEN state.
        var pr = new PullRequestSummary(7, ChildPlanBranch, "https://gh/pr/7", MergedAt: null);
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z");

        var obs = PlanObserver.MapPlanAuthored(
            ChildPlanBranch, branchExists: true, pr, prState: "OPEN",
            mergedAt: null, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Fulfilling);
    }

    [Fact]
    public void MapPlanPromoted_MergedBeforeFilter_DowngradesToNeeded()
    {
        var pr = new PullRequestSummary(8, ChildPlanBranch, "https://gh/pr/8", MergedAt: null);
        var mergedAt = DateTimeOffset.Parse("2026-05-10T12:00:00Z");
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z");

        var obs = PlanObserver.MapPlanPromoted(pr, prState: "MERGED",
            mergedAt: mergedAt, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldContain("prior run");
    }

    [Fact]
    public void MapPlanReviewed_MergedBeforeFilter_DowngradesToNeeded()
    {
        var pr = new PullRequestSummary(9, ChildPlanBranch, "https://gh/pr/9", MergedAt: null);
        var poll = new GhPullRequestPollData(
            Number: 9, State: "MERGED", ReviewDecision: "APPROVED",
            Mergeable: "MERGEABLE", HeadRefName: ChildPlanBranch, HeadRefOid: "abc",
            BaseRefName: "feature/100", MergeCommitSha: "sha",
            MergedAt: DateTimeOffset.Parse("2026-05-10T12:00:00Z"),
            Body: "", Reviews: System.Array.Empty<GhPullRequestReview>(),
            AuthorLogin: "", Comments: System.Array.Empty<GhPullRequestComment>());
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z");

        var obs = PlanObserver.MapPlanReviewed(pr, poll, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldContain("prior run");
    }

    [Fact]
    public void MapImplementationMerged_MergedBeforeFilter_DowngradesToNeeded()
    {
        const string implBranch = "impl/100-200";
        var pr = new PullRequestSummary(10, implBranch, "https://gh/pr/10", MergedAt: null);
        var mergedAt = DateTimeOffset.Parse("2026-05-10T12:00:00Z");
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z");

        var obs = PlanObserver.MapImplementationMerged(implBranch, pr, prState: "MERGED",
            mergedAt: mergedAt, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Needed);
        obs.Reason.ShouldContain("prior run");
        obs.PrNumber.ShouldBe(10);
    }

    [Fact]
    public void MapImplementationMerged_MergedAfterFilter_StaysSatisfied()
    {
        const string implBranch = "impl/100-200";
        var pr = new PullRequestSummary(10, implBranch, "https://gh/pr/10", MergedAt: null);
        var mergedAt = DateTimeOffset.Parse("2026-05-20T12:00:00Z");
        var filter = DateTimeOffset.Parse("2026-05-15T00:00:00Z");

        var obs = PlanObserver.MapImplementationMerged(implBranch, pr, prState: "MERGED",
            mergedAt: mergedAt, runStartedAtFilter: filter);

        obs.Disposition.ShouldBe(Disposition.Satisfied);
    }
}
