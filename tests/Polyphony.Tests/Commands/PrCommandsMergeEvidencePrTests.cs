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
/// Tests for <c>polyphony pr merge-evidence-pr</c> — the unified verb that
/// replaces the bare <c>gh pr merge</c> shell-out previously inlined in
/// <c>actionable.yaml</c>. GitHub leg invokes <c>gh pr merge --squash --auto
/// --delete-branch</c>; ADO leg dispatches to <see cref="PrCommands.MergeEvidenceAdo"/>
/// via the stdout-capture bridge.
/// </summary>
public sealed class PrCommandsMergeEvidencePrTests : CommandTestBase
{
    private const int PrNumber = 42;
    private const string PrUrl = "https://example/pull/42";

    private (PrCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand(bool withAdo = true)
    {
        var ado = withAdo ? new FakeAdoClient() : null;
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
        return (cmd, runner, ado!);
    }

    private static PrMergeEvidenceResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergeEvidenceResult)!;

    [Fact]
    public async Task MergeEvidencePr_MissingPrNumber_RoutesRequiredInputHalt()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(prNumber: int.MinValue));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.MissingArgs.ShouldContain("--pr-number");
    }

    [Fact]
    public async Task MergeEvidencePr_NegativePrNumber_RoutesError()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(prNumber: -1));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Error!.ShouldContain("prNumber must be positive");
    }

    [Fact]
    public async Task MergeEvidencePr_GithubPlatform_DispatchesGhMergeWithAutoSquash()
    {
        var (cmd, runner, _) = CreateCommand();
        runner.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0,
            $$"""{"number":{{PrNumber}},"state":"MERGED","mergeCommit":{"oid":"deadbeef"},"headRefName":"evidence/100","headRefOid":"x"}""", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(
                prNumber: PrNumber, prUrl: PrUrl,
                platform: "github", repository: "owner/repo"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Merged.ShouldBeTrue();
        result.PrNumber.ShouldBe(PrNumber);
        result.PrUrl.ShouldBe(PrUrl);
        result.RepoSlug.ShouldBe("owner/repo");

        var mergeCalls = runner.Invocations.Where(i =>
            i.Executable == "gh" && i.Arguments.Take(2).SequenceEqual(new[] { "pr", "merge" })).ToList();
        mergeCalls.ShouldHaveSingleItem();
        var args = mergeCalls[0].Arguments;
        args.ShouldContain("--squash");
        args.ShouldContain("--auto");
        args.ShouldContain("--delete-branch");
        args.ShouldContain("--repo");
        args.ShouldContain("owner/repo");
    }

    [Fact]
    public async Task MergeEvidencePr_GithubLeg_OriginUrl_ResolvesSlugAutomatically()
    {
        var (cmd, runner, _) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "https://github.com/owner/repo.git\n", ""));
        runner.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0,
            $$"""{"number":{{PrNumber}},"state":"MERGED","mergeCommit":{"oid":"abc"},"headRefName":"evidence/100","headRefOid":"x"}""", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(prNumber: PrNumber));

        exit.ShouldBe(ExitCodes.Success);
        Parse(output).Merged.ShouldBeTrue();
    }

    [Fact]
    public async Task MergeEvidencePr_AdoPlatform_DispatchesToMergeEvidenceAdo()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = new AdoPullRequest(PrNumber, "title", "", "refs/heads/evidence/100", "refs/heads/main",
            "active", null, "user", DateTime.UtcNow, "https://dev.azure.com/o/p/_git/r/pullrequest/42");
        ado.PollData = new AdoPullRequestPollData
        {
            Number = PrNumber,
            State = "OPEN",
            ReviewDecision = "APPROVED",
            Mergeable = "MERGEABLE",
            HeadRefName = "evidence/100",
            HeadRefOid = "headsha",
            BaseRefName = "main",
            Body = "",
            Reviews = [],
        };
        ado.CompleteResult = new AdoCompletePullRequestResult("completed", "newcommit", 200, null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(
                prNumber: PrNumber, prUrl: "",
                platform: "ado", organization: "o", project: "p", repository: "r"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Merged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("newcommit");
        result.Organization.ShouldBe("o");
        result.Project.ShouldBe("p");
        result.Repository.ShouldBe("r");
        result.RepoSlug.ShouldBe("o/p/r");
        ado.CompleteCallCount.ShouldBe(1);
        ado.CompleteStrategy.ShouldBe(AdoMergeStrategy.Squash);
        ado.CompleteDeleteSource.ShouldBeTrue();
    }

    [Fact]
    public async Task MergeEvidencePr_AdoPlatform_PrNotFound_BridgeSurfacesError()
    {
        var (cmd, _, ado) = CreateCommand();
        ado.GetPr = null;

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(
                prNumber: PrNumber, prUrl: "",
                platform: "ado", organization: "o", project: "p", repository: "r"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = Parse(output);
        result.Merged.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task MergeEvidencePr_NoOriginAndNoOverride_RoutesError()
    {
        var (cmd, runner, _) = CreateCommand();
        // `git remote get-url origin` not stubbed → returns failure; GhClient
        // will not be called because resolver fails first.
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: No such remote 'origin'"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergeEvidencePr(prNumber: PrNumber));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Error.ShouldNotBeNullOrEmpty();
    }

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoPullRequest? GetPr { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }
        public int CompleteCallCount { get; private set; }
        public AdoMergeStrategy CompleteStrategy { get; private set; }
        public bool CompleteDeleteSource { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => Task.FromResult(GetPr);

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
            CompleteCallCount++;
            CompleteStrategy = mergeStrategy;
            CompleteDeleteSource = deleteSourceBranch;
            return Task.FromResult(CompleteResult ?? new AdoCompletePullRequestResult("ado_error", null, 500, null));
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
