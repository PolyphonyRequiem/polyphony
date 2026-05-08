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
/// End-to-end tests for <c>polyphony pr create-feature-ado</c> — the Azure
/// DevOps analogue of <c>polyphony pr create-feature-pr</c>. Stubs git
/// ls-remote shell-outs via <see cref="FakeProcessRunner"/> and substitutes
/// <see cref="IAdoClient"/> with a hand-rolled fake. Always exits 0 — error
/// states surface in <c>error_code</c> (routing-style envelope).
/// </summary>
public sealed class PrCommandsCreateFeatureAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";

    private (PrCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand(FakeAdoClient? ado = null)
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
        return (cmd, runner, ado);
    }

    private static void StubLsRemoteHas(FakeProcessRunner runner, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubBranchesExist(FakeProcessRunner runner, string head, string @base)
    {
        StubLsRemoteHas(runner, $"refs/heads/{head}", exists: true);
        StubLsRemoteHas(runner, $"refs/heads/{@base}", exists: true);
    }

    private static PrCreateFeatureAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureAdoResult)!;

    private static AdoPullRequest MakePr(int id, string url, string sourceRef, string targetRef)
        => new(
            PullRequestId: id,
            Title: "title",
            Description: "",
            SourceRefName: sourceRef,
            TargetRefName: targetRef,
            Status: "active",
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: url);

    // ─── Input validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("   ",  "p", "r")]
    public async Task CreateFeatureAdo_WhitespaceIdentifier_RoutesInvalidArgument(string organization, string project, string repository)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(organization, project, repository, rootId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("organization");
    }

    [Theory]
    [InlineData("",  "p", "r", "--organization")]
    [InlineData("o", "",  "r", "--project")]
    [InlineData("o", "p", "",  "--repository")]
    public async Task CreateFeatureAdo_EmptyIdentifier_RoutesInvalidArgument(string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(organization, project, repository, rootId: 100));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr create-feature-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task CreateFeatureAdo_EmptyTargetBranch_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100, targetBranch: ""));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("targetBranch");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task CreateFeatureAdo_InvalidRootId_RoutesInvalidArgument(int rootId)
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task CreateFeatureAdo_NoAdoClient_RoutesAdoFailed()
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
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.Error!.ShouldContain("IAdoClient");
        result.HeadBranch.ShouldBe("feature/100");
        result.BaseBranch.ShouldBe("main");
    }

    // ─── Branch existence ────────────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_HeadMissingOnRemote_RoutesMissingHeadBranch()
    {
        var (cmd, runner, _) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: false);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_head_branch");
        result.Error!.ShouldContain("head branch");
        result.HeadBranch.ShouldBe("feature/100");
        result.BaseBranch.ShouldBe("main");
    }

    [Fact]
    public async Task CreateFeatureAdo_BaseMissingOnRemote_RoutesMissingBaseBranch()
    {
        var (cmd, runner, _) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        StubLsRemoteHas(runner, "refs/heads/main", exists: false);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_base_branch");
        result.Error!.ShouldContain("base branch");
        result.BaseBranch.ShouldBe("main");
    }

    // ─── Default and overridden target branch ────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_DefaultTargetIsMain()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(
            id: 77,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/77",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Created.ShouldBeTrue();
        result.HeadBranch.ShouldBe("feature/100");
        result.BaseBranch.ShouldBe("main");
        result.PrNumber.ShouldBe(77);
    }

    [Fact]
    public async Task CreateFeatureAdo_CustomTargetBranch_Honored()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "develop");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(
            id: 78,
            url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/develop");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100, targetBranch: "develop"));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.HeadBranch.ShouldBe("feature/100");
        result.BaseBranch.ShouldBe("develop");
    }

    // ─── Reuse of existing PR ────────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_ExistingPrForSameHeadBase_Reuses()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(
                id: 50,
                url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/50",
                sourceRef: "refs/heads/feature/100",
                targetRef: "refs/heads/main")
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(50);
        result.PrUrl.ShouldEndWith("/pullrequest/50");
        ado.CreatePrCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task CreateFeatureAdo_ListContainsUnrelatedPrs_FiltersBySourceAndTargetRef()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 1, url: "u1",
                sourceRef: "refs/heads/different/branch",
                targetRef: "refs/heads/main"),
            MakePr(id: 2, url: "u2",
                sourceRef: "refs/heads/feature/100",
                targetRef: "refs/heads/develop"),
        };
        ado.CreatedPr = MakePr(
            id: 99,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/99",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(99);
        ado.CreatePrCallCount.ShouldBe(1);
    }

    // ─── Title and body ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_DefaultTitle_DerivedFromWorkItem()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        // Twig fails (no twig CLI in test runner); falls back to deterministic title.
        result.Title.ShouldContain("100");
        result.Title.ShouldContain("AB#100");
    }

    [Fact]
    public async Task CreateFeatureAdo_ExplicitTitle_OverridesFallback()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100,
                title: "custom feature title"));
        var result = Parse(output);
        result.Title.ShouldBe("custom feature title");
    }

    [Fact]
    public async Task CreateFeatureAdo_DefaultBody_PassedToCreate()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        ado.LastCreateDescription.ShouldNotBeNull();
        ado.LastCreateDescription!.ShouldContain("100");
        ado.LastCreateDescription.ShouldContain("feature/100");
        ado.LastCreateDescription.ShouldContain("main");
    }

    [Fact]
    public async Task CreateFeatureAdo_ExplicitBody_OverridesFallback()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100,
                body: "explicit body content"));
        ado.LastCreateDescription.ShouldBe("explicit body content");
    }

    // ─── PR URL synthesis when ADO returns empty Url ─────────────────────

    [Fact]
    public async Task CreateFeatureAdo_AdoReturnsEmptyUrl_SynthesisesCanonicalUrl()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 42, url: "",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
    }

    // ─── ADO failure mapping ─────────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_ListReturnsNull_RoutesPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrsReturnsNull = true;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task CreateFeatureAdo_NoPat_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new InvalidOperationException("No PAT configured (set AZURE_DEVOPS_EXT_PAT).");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("no_pat");
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Fact]
    public async Task CreateFeatureAdo_Http401_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task CreateFeatureAdo_Http403_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task CreateFeatureAdo_Http404_RoutesAdoFailed()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new HttpRequestException("not found", null, HttpStatusCode.NotFound);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task CreateFeatureAdo_Http5xx_RoutesAdoFailed()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new HttpRequestException("server died", null, HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task CreateFeatureAdo_Timeout_RoutesAdoTimeout()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new TimeoutException("attempts exhausted");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    [Fact]
    public async Task CreateFeatureAdo_CreateReturnsNull_RoutesPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = null;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    // ─── Cancellation propagates ─────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_Cancellation_Propagates()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ThrowOnList = new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateFeatureAdo_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));

        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"head_branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"organization\"");
        output.ShouldContain("\"project\"");
        output.ShouldContain("\"repository\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"title\"");
        output.ShouldContain("\"created\"");
        output.ShouldContain("\"error_code\"");
    }

    [Fact]
    public async Task CreateFeatureAdo_RepoSlug_IsCompositeAdoFormat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubBranchesExist(runner, "feature/100", "main");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(id: 1, url: "u",
            sourceRef: "refs/heads/feature/100",
            targetRef: "refs/heads/main");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeatureAdo(Org, Project, Repo, rootId: 100));
        var result = Parse(output);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public bool ListPrsReturnsNull { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequest? CreatedPr { get; set; }
        public int CreatePrCallCount { get; private set; }
        public string? LastCreateDescription { get; private set; }

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
        {
            CreatePrCallCount++;
            LastCreateDescription = description;
            return Task.FromResult(CreatedPr);
        }

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
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
            => throw new NotImplementedException();
}
}
