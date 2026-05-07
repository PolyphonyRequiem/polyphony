using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr merge-mg-ado</c> — the Azure DevOps
/// analogue of <c>polyphony pr merge-mg-pr</c>. Stubs <see cref="IAdoClient"/>
/// with a hand-rolled fake. Always exits 0 — error states surface in
/// <c>error_code</c> (routing-style envelope).
/// </summary>
public sealed class PrCommandsMergeMgAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

    private (PrCommands Command, FakeAdoClient Ado) CreateCommand(FakeAdoClient? ado = null)
    {
        ado ??= new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            ado);
        return (cmd, ado);
    }

    private static PrMergeMgAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeMgAdoResult)!;

    private static AdoPullRequest MakePr(int id, string url, string sourceRef, string targetRef,
        string status = "active")
        => new(
            PullRequestId: id,
            Title: "title",
            Description: "",
            SourceRefName: sourceRef,
            TargetRefName: targetRef,
            Status: status,
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: url);

    private static AdoPullRequestPollData MakePoll(
        int number, string state, string headRef, string baseRef,
        string headOid = "abc123", string? mergeCommit = null)
        => new()
        {
            Number = number,
            State = state,
            ReviewDecision = "APPROVED",
            Mergeable = "MERGEABLE",
            HeadRefName = headRef,
            HeadRefOid = headOid,
            BaseRefName = baseRef,
            MergedAt = state == "MERGED" ? DateTime.UtcNow : null,
            MergeCommit = mergeCommit,
            Body = "",
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    // Convenience: seed the active PR list with a single PR that matches a
    // top-level mg/100_core → feature/100 pair.
    private static void SeedActivePr(FakeAdoClient ado, int prId = 42, string url = "")
    {
        url = string.IsNullOrEmpty(url) ? $"https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/{prId}" : url;
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(prId, url,
                sourceRef: "refs/heads/mg/100_core",
                targetRef: "refs/heads/feature/100"),
        };
        ado.PollData = MakePoll(prId, "OPEN", "mg/100_core", "feature/100");
    }

    // ─── Input validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("",     "p", "r")]
    [InlineData("o",    "",  "r")]
    [InlineData("o",    "p", "")]
    [InlineData("   ",  "p", "r")]
    public async Task MergeMgAdo_EmptyIdentifier_RoutesInvalidArgument(string organization, string project, string repository)
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(organization, project, repository, rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("organization");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MergeMgAdo_InvalidRootId_RoutesInvalidArgument(int rootId)
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("rootId");
    }

    [Theory]
    [InlineData("BAD")]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("a__b")]
    public async Task MergeMgAdo_InvalidMgPath_RoutesInvalidArgument(string mgPath)
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: mgPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task MergeMgAdo_NoAdoClient_RoutesAdoFailed()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            ado: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.HeadBranch.ShouldBe("mg/100_core");
        result.BaseBranch.ShouldBe("feature/100");
    }

    // ─── PR resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_NoActivePr_RoutesPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>();  // no PRs at all

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error!.ShouldContain("No active PR found");
    }

    [Fact]
    public async Task MergeMgAdo_ListReturnsNull_RoutesPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrsReturnsNull = true;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeMgAdo_OnlyUnrelatedPrs_RoutesPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(1, "u", "refs/heads/mg/999_other", "refs/heads/feature/999"),
            MakePr(2, "u", "refs/heads/mg/100_core", "refs/heads/different"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeMgAdo_CompletedPrInList_TreatedAsAlreadyMerged()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42",
                sourceRef: "refs/heads/mg/100_core",
                targetRef: "refs/heads/feature/100",
                status: "completed"),
        };
        // The verb re-reads the merge commit via the poll-data endpoint.
        ado.PollData = MakePoll(42, "MERGED", "mg/100_core", "feature/100",
            mergeCommit: "preexisting-sha");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("preexisting-sha");
        result.PrState.ShouldBe("MERGED");
        ado.CompleteCallCount.ShouldBe(0);  // already merged: no complete call
    }

    [Fact]
    public async Task MergeMgAdo_CompletedPrButNoMergeSha_RoutesMissingMergeCommit()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "u",
                sourceRef: "refs/heads/mg/100_core",
                targetRef: "refs/heads/feature/100",
                status: "completed"),
        };
        ado.PollData = MakePoll(42, "MERGED", "mg/100_core", "feature/100",
            mergeCommit: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("missing_merge_commit");
    }

    // ─── Live MERGED state ───────────────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_PollReportsMerged_ReusesMergeShaWithoutComplete()
    {
        var (cmd, ado) = CreateCommand();
        // PR is "active" in list (not yet completed) but live poll returns MERGED.
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "u", "refs/heads/mg/100_core", "refs/heads/feature/100"),
        };
        ado.PollData = MakePoll(42, "MERGED", "mg/100_core", "feature/100",
            mergeCommit: "live-merge-sha");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("live-merge-sha");
        ado.CompleteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task MergeMgAdo_PollMergedButNoSha_RoutesMissingMergeCommit()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "u", "refs/heads/mg/100_core", "refs/heads/feature/100"),
        };
        ado.PollData = MakePoll(42, "MERGED", "mg/100_core", "feature/100",
            mergeCommit: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("missing_merge_commit");
    }

    [Fact]
    public async Task MergeMgAdo_PollReportsClosed_RoutesPrStateInvalid()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "u", "refs/heads/mg/100_core", "refs/heads/feature/100"),
        };
        ado.PollData = MakePoll(42, "CLOSED", "mg/100_core", "feature/100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_state_invalid");
        result.PrState.ShouldBe("CLOSED");
    }

    [Fact]
    public async Task MergeMgAdo_PollReturnsNull_RoutesPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "u", "refs/heads/mg/100_core", "refs/heads/feature/100"),
        };
        ado.PollData = null;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error!.ShouldContain("disappeared");
    }

    // ─── Stale-head pre-check (--match-head-commit) ──────────────────────

    [Fact]
    public async Task MergeMgAdo_MatchHeadCommitMatches_ProceedsToComplete()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.PollData = MakePoll(42, "OPEN", "mg/100_core", "feature/100", headOid: "matching-sha");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "merge-sha", HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core",
                matchHeadCommit: "matching-sha"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("merge-sha");
        ado.CompleteCallCount.ShouldBe(1);
        ado.LastHeadShaSent.ShouldBe("matching-sha");
    }

    [Fact]
    public async Task MergeMgAdo_MatchHeadCommitMismatches_RoutesStaleHead()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.PollData = MakePoll(42, "OPEN", "mg/100_core", "feature/100", headOid: "live-sha");
        // CompleteResult intentionally not set — must NOT be invoked.

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core",
                matchHeadCommit: "stale-sha"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_head");
        result.Error!.ShouldContain("stale-sha");
        result.Error!.ShouldContain("live-sha");
        ado.CompleteCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task MergeMgAdo_NoMatchHeadCommit_UsesPolledHeadSha()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.PollData = MakePoll(42, "OPEN", "mg/100_core", "feature/100", headOid: "polled-sha");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "merge-sha", HttpStatus: 200, ErrorBody: null);

        await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        ado.CompleteCallCount.ShouldBe(1);
        ado.LastHeadShaSent.ShouldBe("polled-sha");
    }

    // ─── Successful merge ────────────────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_TopLevel_SuccessfulMerge()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "merge-sha-xyz", HttpStatus: 200, ErrorBody: null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.MergeCommit.ShouldBe("merge-sha-xyz");
        result.PrNumber.ShouldBe(42);
        result.HeadBranch.ShouldBe("mg/100_core");
        result.BaseBranch.ShouldBe("feature/100");
        result.Method.ShouldBe("merge");
        result.DeleteBranch.ShouldBeFalse();
        result.PrState.ShouldBe("MERGED");
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
    }

    [Fact]
    public async Task MergeMgAdo_Nested_BaseIsParentMgBranch()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(50, "u",
                sourceRef: "refs/heads/mg/100_core_api",
                targetRef: "refs/heads/mg/100_core"),
        };
        ado.PollData = MakePoll(50, "OPEN", "mg/100_core_api", "mg/100_core");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "merge", HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core_api"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.HeadBranch.ShouldBe("mg/100_core_api");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task MergeMgAdo_AdoReturnsEmptyUrl_SynthesisesCanonicalUrl()
    {
        var (cmd, ado) = CreateCommand();
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(42, "",  // empty url
                sourceRef: "refs/heads/mg/100_core",
                targetRef: "refs/heads/feature/100"),
        };
        ado.PollData = MakePoll(42, "OPEN", "mg/100_core", "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "x", HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
    }

    // ─── Complete-PR routable failures ───────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_CompleteStaleHead_RoutesStaleHead()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "stale_head", MergeCommitSha: null, HttpStatus: 409,
            ErrorBody: "head moved");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_head");
        result.Error!.ShouldContain("source branch advanced");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteNotFound_RoutesPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "not_found", MergeCommitSha: null, HttpStatus: 404, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteNotMergeable_RoutesAdoCompleteFailed()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "not_mergeable", MergeCommitSha: null, HttpStatus: 400,
            ErrorBody: "policy refused");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_complete_failed");
        result.Error!.ShouldContain("policy refused");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteAdoError_RoutesAdoCompleteFailed()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "ado_error", MergeCommitSha: null, HttpStatus: 500, ErrorBody: "boom");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_complete_failed");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteSuccessButNoMergeSha_RoutesMissingMergeCommit()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: null, HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_merge_commit");
        result.Merged.ShouldBeTrue();
    }

    // ─── Wire-level failures: list ───────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_ListNoPat_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new InvalidOperationException("PAT required (set AZURE_DEVOPS_EXT_PAT)");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_ListHttp401_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_ListHttp403_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_ListHttp5xx_RoutesAdoFailed()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new HttpRequestException("server died", null, HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task MergeMgAdo_ListTimeout_RoutesAdoTimeout()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new TimeoutException("attempts exhausted");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    // ─── Wire-level failures: poll ───────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_PollNoPat_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnPoll = new InvalidOperationException("PAT required");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_PollHttp401_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnPoll = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_PollHttp5xx_RoutesAdoFailed()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnPoll = new HttpRequestException("boom", null, HttpStatusCode.InternalServerError);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task MergeMgAdo_PollTimeout_RoutesAdoTimeout()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnPoll = new TimeoutException("timeout");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    // ─── Wire-level failures: complete ───────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_CompleteNoPat_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnComplete = new InvalidOperationException("PAT required");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteHttp401_RoutesNoPat()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnComplete = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteHttp5xx_RoutesAdoCompleteFailed()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnComplete = new HttpRequestException("boom", null, HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_complete_failed");
    }

    [Fact]
    public async Task MergeMgAdo_CompleteTimeout_RoutesAdoTimeout()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnComplete = new TimeoutException("timeout");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    // ─── Cancellation ────────────────────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_CancellationOnList_Propagates()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnList = new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
    }

    [Fact]
    public async Task MergeMgAdo_CancellationOnComplete_Propagates()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.ThrowOnComplete = new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task MergeMgAdo_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, ado) = CreateCommand();
        SeedActivePr(ado);
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "x", HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergeMgAdo(Org, Project, Repo, rootId: 100, mgPath: "core"));

        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"mg_path\"");
        output.ShouldContain("\"head_branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"organization\"");
        output.ShouldContain("\"project\"");
        output.ShouldContain("\"repository\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"pr_state\"");
        output.ShouldContain("\"method\"");
        output.ShouldContain("\"merged\"");
        output.ShouldContain("\"already_merged\"");
        output.ShouldContain("\"delete_branch\"");
        output.ShouldContain("\"merge_commit\"");
        output.ShouldContain("\"error_code\"");
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public bool ListPrsReturnsNull { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public Exception? ThrowOnPoll { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }
        public Exception? ThrowOnComplete { get; set; }
        public int CompleteCallCount { get; private set; }
        public string? LastHeadShaSent { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            CancellationToken ct = default)
        {
            if (ThrowOnList is not null) throw ThrowOnList;
            if (ListPrsReturnsNull) return Task.FromResult<IReadOnlyList<AdoPullRequest>?>(null);
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
        {
            if (ThrowOnPoll is not null) throw ThrowOnPoll;
            return Task.FromResult(PollData);
        }

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            CancellationToken ct = default)
        {
            CompleteCallCount++;
            LastHeadShaSent = lastMergeSourceCommitSha;
            if (ThrowOnComplete is not null) throw ThrowOnComplete;
            if (CompleteResult is null)
                throw new InvalidOperationException("Test fake: CompleteResult not configured.");
            return Task.FromResult(CompleteResult);
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
}
}
