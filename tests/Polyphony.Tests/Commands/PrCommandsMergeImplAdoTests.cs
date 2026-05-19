using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Smoke tests for <c>polyphony pr merge-impl-ado</c> — squash-merge the
/// per-item impl PR into its enclosing merge group on Azure DevOps.
/// </summary>
public sealed class PrCommandsMergeImplAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

    private (PrCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand()
    {
        var ado = new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            ado);
        return (cmd, runner, ado);
    }

    private static PrMergeImplAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeImplAdoResult)!;

    private static AdoPullRequest MakePr(int id, string status, string sourceRef, string targetRef)
        => new(id, "title", "", sourceRef, targetRef, status, null, "user", DateTime.UtcNow, "");

    // ─── Branch-recycle staleness check helpers (AB#3211) ────────────────
    // ValidateCompletedAdoPrAsync (PrCommands.MergeShared.cs) calls
    // `git ls-remote --heads origin refs/heads/{head}` and, when the head
    // is gone, `git merge-base --is-ancestor {mergeSha} origin/{base}`.
    // FakeProcessRunner throws on unmatched invocations, so any test that
    // exercises the completed-PR short-circuit MUST stub these.
    private static void StubOriginBranchSha(FakeProcessRunner runner, string branch, string sha)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, $"{sha}\trefs/heads/{branch}\n", ""));

    private static void StubOriginBranchMissing(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, "", ""));

    private static void StubIsAncestor(FakeProcessRunner runner, string maybeAncestor, string descendant, bool isAncestor)
        => runner.WhenExact("git", ["merge-base", "--is-ancestor", maybeAncestor, descendant],
            new ProcessResult(isAncestor ? 0 : 1, "", ""));

    private static AdoPullRequestPollData MakePoll(string state, string headOid, string? mergeCommit = null)
        => new()
        {
            Number = 1,
            State = state,
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "MERGEABLE",
            HeadRefName = "impl/100-200",
            HeadRefOid = headOid,
            BaseRefName = "mg/100_core",
            MergedAt = state == "MERGED" ? DateTime.UtcNow : null,
            MergeCommit = mergeCommit,
            Body = "",
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    [Theory]
    [InlineData("", "p", "r", "--organization")]
    [InlineData("o", "", "r", "--project")]
    [InlineData("o", "p", "", "--repository")]
    public async Task MergeImplAdo_EmptyIdentifier_RoutesRequiredInputHalt(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(organization, project, repository,
                rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr merge-impl-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task MergeImplAdo_InvalidRootId_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 0, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task MergeImplAdo_InvalidMgPath_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "BAD"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task MergeImplAdo_NoAdoClient_RoutesAdoFailed()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            ado: null);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.HeadBranch.ShouldBe("impl/100-200");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task MergeImplAdo_PrNotFound_RoutesPrNotFound()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeImplAdo_AlreadyCompletedPr_RoutesAlreadyMerged()
    {
        var (cmd, runner, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 77, status: "completed",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "MERGED", headOid: "deadbeef", mergeCommit: "merge-sha-7");
        // Validity clause 1: origin/{head} matches PR's recorded source SHA.
        StubOriginBranchSha(runner, "impl/100-200", "deadbeef");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("merge-sha-7");
        result.Method.ShouldBe("squash");
        result.DeleteBranch.ShouldBeTrue();
    }

    // AB#3211: yesterday's completed PR for the same branch pair must NOT
    // short-circuit today's verb when origin/{head} now points at a
    // different commit (post-reset branch reuse). The verb falls through
    // to the "no active PR" arm and reports pr_not_found so the workflow
    // proceeds to open a fresh PR.
    [Fact]
    public async Task MergeImplAdo_CompletedPrSourceShaStale_FallsThroughToPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 77, status: "completed",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "MERGED", headOid: "yesterday-sha", mergeCommit: "yesterday-merge");
        // Clause 1 fails: origin/{head} no longer points at the PR's recorded source SHA.
        StubOriginBranchSha(runner, "impl/100-200", "today-sha");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    // AB#3211 clause 2: head was deleted on PR completion (typical), but
    // the merge commit is reachable from origin/{base} -- still safe to
    // short-circuit, because the merge actually landed.
    [Fact]
    public async Task MergeImplAdo_CompletedPrHeadDeletedMergeReachable_HonorsAlreadyMerged()
    {
        var (cmd, runner, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 77, status: "completed",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "MERGED", headOid: "deleted-sha", mergeCommit: "real-merge");
        StubOriginBranchMissing(runner, "impl/100-200");
        StubIsAncestor(runner, "real-merge", "origin/mg/100_core", true);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("real-merge");
    }

    // AB#3211 clause 2 negative: head was deleted AND merge commit isn't
    // on origin/{base}. The most damaging case from apex 62286666: the
    // PR was for a different run, the branch was recycled, and the
    // recorded merge commit no longer lives on today's base branch.
    [Fact]
    public async Task MergeImplAdo_CompletedPrHeadDeletedMergeNotReachable_FallsThroughToPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 77, status: "completed",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "MERGED", headOid: "deleted-sha", mergeCommit: "orphan-merge");
        StubOriginBranchMissing(runner, "impl/100-200");
        StubIsAncestor(runner, "orphan-merge", "origin/mg/100_core", false);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeImplAdo_HappyPath_CompletesViaSquashAndDeletesBranch()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 200, Status: "completed", MergeCommitSha: "merge-sha-9", ErrorBody: "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.MergeCommit.ShouldBe("merge-sha-9");
        result.Method.ShouldBe("squash");
        result.DeleteBranch.ShouldBeTrue();
        ado.LastCompleteStrategy.ShouldBe(AdoMergeStrategy.Squash);
        ado.LastCompleteDeleteSourceBranch.ShouldBeTrue();
        ado.LastCompleteSourceSha.ShouldBe("head-sha-1");
    }

    [Fact]
    public async Task MergeImplAdo_DeleteBranchFalse_PassesDeleteSourceBranchFalseToAdo()
    {
        // Regression for the v2.4.1 CAF-bool wiring bug: when
        // `polyphony pr merge-impl-ado --delete-branch false` was rejected
        // at parse time, the impl PR squash never ran, MG branch never
        // advanced, and `assert_impl_pr_coverage` correctly fired on the
        // gap. This test pins the threaded-through behavior end-to-end:
        // `--delete-branch false` ⇒ `deleteSourceBranch: false` on the
        // ADO complete-PR call.
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 200, Status: "completed", MergeCommitSha: "merge-sha-9", ErrorBody: "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core",
                deleteBranch: "false"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.DeleteBranch.ShouldBeFalse();
        ado.LastCompleteDeleteSourceBranch.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeImplAdo_DeleteBranchCaseInsensitive_AcceptsTrueFalse()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 200, Status: "completed", MergeCommitSha: "merge-sha-9", ErrorBody: "");

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core",
                deleteBranch: "FALSE"));
        exit.ShouldBe(ExitCodes.Success);
        ado.LastCompleteDeleteSourceBranch.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeImplAdo_DeleteBranchGarbage_EmitsDispatchErrorEnvelope()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core",
                deleteBranch: "yes"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr merge-impl-ado");
        envelope.Error.ShouldContain("--delete-branch");
        envelope.Error.ShouldContain("'yes'");
        envelope.MissingArgs.ShouldContain("--delete-branch");
    }

    [Fact]
    public async Task MergeImplAdo_StaleHeadFromMatchHeadCommit_RoutesStaleHead()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "live-head-sha");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core",
                matchHeadCommit: "expected-stale-sha"));
        Parse(output).ErrorCode.ShouldBe("stale_head");
    }

    [Fact]
    public async Task MergeImplAdo_StaleHeadFromCompleteCall_RoutesStaleHead()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 409, Status: "stale_head", MergeCommitSha: "", ErrorBody: "branch advanced");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("stale_head");
    }

    [Fact]
    public async Task MergeImplAdo_CompletionPending_RoutesCompletionPendingError()
    {
        // Regression for the AdoClient false-positive-merge bug: when ADO
        // accepted the PATCH but the PR didn't actually transition to
        // status=completed, the client surfaces "completion_pending" and
        // the verb must route to a distinct error_code (not silently
        // pretending the merge landed).
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "OPEN", headOid: "head-sha-1");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            HttpStatus: 200, Status: "completion_pending", MergeCommitSha: null,
            ErrorBody: "PR #88 did not transition to status=completed within the poll budget.");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("completion_pending");
        result.Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeImplAdo_PrInClosedState_RoutesPrStateInvalid()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 88, status: "active",
                sourceRef: "refs/heads/impl/100-200",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(state: "CLOSED", headOid: "x");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_state_invalid");
    }

    [Fact]
    public async Task MergeImplAdo_NoPat_RoutesNoPat()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.ThrowOnList = new HttpRequestException("u", null, HttpStatusCode.Unauthorized);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeImplAdo(Org, Project, Repo, rootId: 100, itemId: 200, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }

        public AdoMergeStrategy? LastCompleteStrategy { get; private set; }
        public bool LastCompleteDeleteSourceBranch { get; private set; }
        public string LastCompleteSourceSha { get; private set; } = "";

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null, CancellationToken ct = default)
        {
            if (ThrowOnList is not null) throw ThrowOnList;
            return Task.FromResult<IReadOnlyList<AdoPullRequest>?>(ListPrs ?? new List<AdoPullRequest>());
        }

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title,
            string description, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
            => Task.FromResult(PollData);

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            AdoMergeStrategy mergeStrategy, bool deleteSourceBranch,
            CancellationToken ct = default)
        {
            LastCompleteStrategy = mergeStrategy;
            LastCompleteDeleteSourceBranch = deleteSourceBranch;
            LastCompleteSourceSha = lastMergeSourceCommitSha;
            return Task.FromResult(CompleteResult ?? new AdoCompletePullRequestResult("ado_failed", null, 0, ""));
        }

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> EditPullRequestBodyAsync(
            string organization, string project, string repository,
            int pullRequestId, string body, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> ClosePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBeforeClose, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
