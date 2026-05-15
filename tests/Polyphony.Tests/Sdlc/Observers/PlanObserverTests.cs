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
}
