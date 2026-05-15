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
/// End-to-end tests for <c>polyphony pr poll-status-ado</c>. Stubs
/// <see cref="IAdoClient"/> directly (the verb only consumes one method on
/// it) and asserts on the platform-neutral JSON the verb emits — same
/// shape as the GitHub-side <c>pr poll-status</c> verb, with the addition
/// of an <c>error_code</c> envelope field for stable workflow routing.
/// </summary>
public sealed class PrCommandsPollStatusAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;

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
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado);
        return (cmd, ado);
    }

    private static PrPollStatusResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrPollStatusResult)!;

    private static AdoPullRequestPollData OpenApprovedData(string body = "") => new()
    {
        Number = PrId,
        State = "OPEN",
        ReviewDecision = "APPROVED",
        Mergeable = "MERGEABLE",
        HeadRefName = "feature/x",
        HeadRefOid = "abc123",
        BaseRefName = "main",
        MergedAt = null,
        MergeCommit = null,
        Body = body,
        Reviews =
        [
            new AdoPullRequestReview { Identity = "Alice", Vote = "approved", SubmittedAt = null },
        ],
    };

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollStatusAdo_HappyPath_EmitsApprovedEnvelope()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.PrNumber.ShouldBe(PrId);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
        result.HeadSha.ShouldBe("abc123");
        result.HeadRef.ShouldBe("feature/x");
        result.BaseRef.ShouldBe("main");
        result.Mergeable.ShouldBe(true);
        result.Reviewers.Count.ShouldBe(1);
        result.Reviewers[0].Identity.ShouldBe("Alice");
        result.Reviewers[0].Vote.ShouldBe("approved");
        result.Policy.MergeAllowed.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();
        ado.LastOrganization.ShouldBe(Org);
        ado.LastProject.ShouldBe(Project);
        ado.LastRepository.ShouldBe(Repo);
        ado.LastPrId.ShouldBe(PrId);
    }

    [Fact]
    public async Task PollStatusAdo_MergedState_NormalizesToMerged()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData() with
        {
            State = "MERGED",
            MergedAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc),
            MergeCommit = "deadbeef",
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.State.ShouldBe("merged");
        result.MergeCommitSha.ShouldBe("deadbeef");
        result.MergedAt.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task PollStatusAdo_ClosedState_NormalizesToClosed()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData() with
        {
            State = "CLOSED",
            ReviewDecision = "REVIEW_REQUIRED",
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.State.ShouldBe("closed");
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatusAdo_RejectedDecision_NormalizesToChangesRequested()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData() with { ReviewDecision = "REJECTED" };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.State.ShouldBe("changes_requested");
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatusAdo_ConflictingMergeable_BlocksMerge()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData() with { Mergeable = "CONFLICTING" };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.Mergeable.ShouldBe(false);
        result.Policy.MergeAllowed.ShouldBeFalse();
        result.Policy.BlockingReasons.ShouldContain(x => x.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Error envelopes ─────────────────────────────────────────────────

    [Fact]
    public async Task PollStatusAdo_PrNotFound_EmitsPrNotFoundErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = null; // mimics IAdoClient returning null on 404

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
        result.PrNumber.ShouldBe(PrId);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
    }

    [Fact]
    public async Task PollStatusAdo_AdoTimeout_EmitsAdoTimeoutErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnPoll = new TimeoutException("ADO request timed out after 3 attempt(s).");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("ado_timeout");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("timed out");
    }

    [Fact]
    public async Task PollStatusAdo_NoPat_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnPoll = new InvalidOperationException(
            "No ADO PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login').");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("no_pat");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Fact]
    public async Task PollStatusAdo_Unauthorized_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnPoll = new HttpRequestException(
            "ADO request failed: HTTP 401 Unauthorized",
            inner: null,
            statusCode: HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task PollStatusAdo_5xx_EmitsAdoFailedErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnPoll = new HttpRequestException(
            "ADO request failed: HTTP 502 Bad Gateway",
            inner: null,
            statusCode: HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("ado_failed");
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("   ",    Project, Repo)]
    public async Task PollStatusAdo_WhitespaceRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository)
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData(); // would succeed if invoked

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(organization, project, repository, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("invalid_argument");
        ado.PollCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("",       Project, Repo,    "--organization")]
    [InlineData(Org,      "",      Repo,    "--project")]
    [InlineData(Org,      Project, "",      "--repository-id")]
    public async Task PollStatusAdo_EmptyRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData(); // would succeed if invoked

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(organization, project, repository, PrId));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr poll-status-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
        ado.PollCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task PollStatusAdo_NonPositivePrNumber_EmitsInvalidArgument()
    {
        var (cmd, ado) = CreateCommand();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, 0));
        var result = Parse(output);

        result.State.ShouldBe("error");
        result.ErrorCode.ShouldBe("invalid_argument");
        ado.PollCallCount.ShouldBe(0);
    }

    // ─── Metadata flag ───────────────────────────────────────────────────

    [Fact]
    public async Task PollStatusAdo_WithoutMetadataFlag_LeavesMetadataNull()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData(
            body: "---\nrequests_parent_change: true\n---\n## Body");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task PollStatusAdo_WithMetadataFlag_ParsesFrontMatter()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData(
            body: "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 2\n  \"5678\": 1\n---\n## Body");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId, includeMetadata: true));
        var result = Parse(output);

        result.Metadata.ShouldNotBeNull();
        result.Metadata!.RequestsParentChange.ShouldBeTrue();
        result.Metadata.AncestorPlanGenerations.Count.ShouldBe(2);
        result.Metadata.AncestorPlanGenerations["root"].ShouldBe(2);
        result.Metadata.AncestorPlanGenerations["5678"].ShouldBe(1);
    }

    [Fact]
    public async Task PollStatusAdo_WithMetadataFlag_NoFrontMatter_ReturnsDefaults()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData(body: "Just a regular PR body.");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId, includeMetadata: true));
        var result = Parse(output);

        result.Metadata.ShouldNotBeNull();
        result.Metadata!.RequestsParentChange.ShouldBeFalse();
        result.Metadata.AncestorPlanGenerations.ShouldBeEmpty();
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task PollStatusAdo_JsonContract_IsSnakeCase()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        // Must match the GitHub-side envelope shape — workflow YAML reads these keys.
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"head_sha\"");
        output.ShouldContain("\"head_ref\"");
        output.ShouldContain("\"base_ref\"");
        output.ShouldContain("\"reviewers\"");
        output.ShouldContain("\"policy\"");
        output.ShouldContain("\"merge_allowed\"");
        output.ShouldContain("\"blocking_reasons\"");
    }

    // ─── Option B — review-thread-driven derivation (ADO) ────────────────

    private static AdoPullRequestThread MakeAdoThread(int id, bool isResolved, string status = "active")
        => new()
        {
            Id = id,
            Status = status,
            IsResolved = isResolved,
            FilePath = "src/foo.cs",
            Line = 10,
            Comments =
            [
                new AdoPullRequestComment
                {
                    Id = id * 10,
                    ParentCommentId = 0,
                    Author = "Alice",
                    Body = "needs work",
                    PublishedAt = new DateTime(2026, 5, 8, 19, 0, 0, DateTimeKind.Utc),
                    LastUpdatedAt = new DateTime(2026, 5, 8, 19, 0, 0, DateTimeKind.Utc),
                    CommentType = "text",
                },
            ],
        };

    [Fact]
    public async Task PollStatusAdo_UnresolvedThread_OverridesNativeApproved()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData();
        ado.Threads = [MakeAdoThread(1, isResolved: false, status: "active")];

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
        result.Threads.ShouldHaveSingleItem();
        result.Threads[0].IsResolved.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatusAdo_AllResolvedThreads_PlusApproved_Approved()
    {
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData();
        ado.Threads = [MakeAdoThread(1, isResolved: true, status: "fixed")];

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    [Fact]
    public async Task PollStatusAdo_ListThreadsThrows_AppendsWarning_StillDerives()
    {
        // Network/transient ADO failure on the threads call must not make the
        // verb error out — derivation falls back to native review state and
        // the failure is surfaced as a warning.
        var (cmd, ado) = CreateCommand();
        ado.PollResult = OpenApprovedData();
        ado.ThrowOnListThreads = new InvalidOperationException("transient ADO 500");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatusAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Warnings.ShouldNotBeNull();
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoPullRequestPollData? PollResult { get; set; }
        public Exception? ThrowOnPoll { get; set; }
        public IReadOnlyList<AdoPullRequestThread>? Threads { get; set; } = Array.Empty<AdoPullRequestThread>();
        public Exception? ThrowOnListThreads { get; set; }
        public int PollCallCount { get; private set; }
        public string? LastOrganization { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public int LastPrId { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            CancellationToken ct = default)
            => throw new NotImplementedException();

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
            PollCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repositoryId;
            LastPrId = pullRequestId;
            if (ThrowOnPoll is not null) throw ThrowOnPoll;
            return Task.FromResult(PollResult);
        }

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
            => throw new NotImplementedException();

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    
        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
        {
            if (ThrowOnListThreads is not null)
            {
                throw ThrowOnListThreads;
            }
            return Task.FromResult(Threads);
        }

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
