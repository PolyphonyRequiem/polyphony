using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr open-plan-ado</c>. Uses a
/// hand-rolled <see cref="IAdoClient"/> fake (the verb only consumes
/// three methods on it: <see cref="IAdoClient.ListPullRequestsAsync"/>,
/// <see cref="IAdoClient.GetPullRequestPollDataAsync"/>, and
/// <see cref="IAdoClient.CreatePullRequestAsync"/>) and stubs all
/// shell-outs (git show, twig show) via <see cref="FakeProcessRunner"/>.
/// Always exits 0 — error states surface in <c>error_code</c>
/// (routing-style envelope).
/// </summary>
public sealed class PrCommandsOpenPlanAdoTests : CommandTestBase
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
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado);
        return (cmd, runner, ado);
    }

    private static string SeedManifest(FakeProcessRunner runner, int rootId,
        Dictionary<string, int>? planGenerations = null)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "polyphony-tests-ado-" + Guid.NewGuid().ToString("N") + ".yaml");
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "dev.azure.com/myorg/myproj",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal),
        };
        RunManifestStore.Save(path, manifest);
        var yaml = File.ReadAllText(path);
        runner.WhenExact("git", ["show", $"origin/feature/{rootId}:{path}"],
            new ProcessResult(0, yaml, ""));
        return path;
    }

    private static void StubManifestMissing(FakeProcessRunner runner, int rootId, string path)
        => runner.WhenExact("git", ["show", $"origin/feature/{rootId}:{path}"],
            new ProcessResult(128, "", $"fatal: path '{path}' does not exist in 'origin/feature/{rootId}'"));

    private static void StubTwigShow(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--tree", "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static PrOpenPlanAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenPlanAdoResult)!;

    /// <summary>
    /// Build an <see cref="AdoPullRequest"/> with sensible defaults — only
    /// the fields the verb cares about (id, url, source/target ref, description)
    /// vary per test.
    /// </summary>
    private static AdoPullRequest MakePr(
        int id, string url, string sourceRef, string targetRef, string description = "")
        => new(
            PullRequestId: id,
            Title: "title",
            Description: description,
            SourceRefName: sourceRef,
            TargetRefName: targetRef,
            Status: "active",
            MergeStatus: null,
            CreatedBy: "user",
            CreationDate: DateTime.UtcNow,
            Url: url);

    private static AdoPullRequestPollData MakePollData(int number, string headRef, string baseRef, string body)
        => new()
        {
            Number = number,
            State = "OPEN",
            ReviewDecision = "REVIEW_REQUIRED",
            Mergeable = "MERGEABLE",
            HeadRefName = headRef,
            HeadRefOid = "abc",
            BaseRefName = baseRef,
            MergedAt = null,
            MergeCommit = null,
            Body = body,
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    // ─── Input validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("   ",  "p", "r")]
    public async Task OpenPlanAdo_WhitespaceIdentifier_RoutesInvalidArgument(string organization, string project, string repository)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(organization, project, repository, rootId: 100, itemId: 100,
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("",  "p", "r", "--organization")]
    [InlineData("o", "",  "r", "--project")]
    [InlineData("o", "p", "",  "--repository")]
    public async Task OpenPlanAdo_EmptyIdentifier_RoutesInvalidArgument(string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(organization, project, repository, rootId: 100, itemId: 100,
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr open-plan-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(-1, 100, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(100, -5, 0)]
    public async Task OpenPlanAdo_InvalidIds_RoutesInvalidArgument(int rootId, int itemId, int parentItemId)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId, itemId, parentItemId,
                manifestPath: "irrelevant.yaml"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task OpenPlanAdo_RootPlanWithParentItemId_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, parentItemId: 50,
                manifestPath: "irrelevant.yaml"));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("--parent-item-id must not be provided");
    }

    [Fact]
    public async Task OpenPlanAdo_RootPlanWithAncestors_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, ancestorIds: "5678,root",
                manifestPath: "irrelevant.yaml"));
        Parse(output).Error!.ShouldContain("root plan must not declare ancestors");
    }

    [Fact]
    public async Task OpenPlanAdo_DescendantWithoutAncestors_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, ancestorIds: "",
                manifestPath: "irrelevant.yaml"));
        Parse(output).Error!.ShouldContain("--ancestor-ids must list");
    }

    [Fact]
    public async Task OpenPlanAdo_ParentEqualsRoot_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, parentItemId: 100, ancestorIds: "root",
                manifestPath: "irrelevant.yaml"));
        Parse(output).Error!.ShouldContain("omit --parent-item-id");
    }

    // ─── Manifest read errors ────────────────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_ManifestMissing_RoutesManifestReadFailed()
    {
        var (cmd, _, _) = CreateCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(),
            "polyphony-missing-" + Guid.NewGuid().ToString("N") + ".yaml");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: bogusPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_read_failed");
        result.Error!.ShouldContain("manifest not found at");
    }

    // ─── Happy paths: create new PR ──────────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_RootPlan_CreatesNewPr()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Authentication overhaul");
        ado.ListPrs = new List<AdoPullRequest>();  // no existing
        ado.CreatedPr = MakePr(
            id: 42,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42",
            sourceRef: "refs/heads/plan/100",
            targetRef: "refs/heads/feature/100");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Created.ShouldBeTrue();
        result.Stale.ShouldBeFalse();
        result.IsRootPlan.ShouldBeTrue();
        result.HeadBranch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
        result.Organization.ShouldBe(Org);
        result.Project.ShouldBe(Project);
        result.Repository.ShouldBe(Repo);
        result.ItemKey.ShouldBe("root");
        result.AncestorPlanGenerations.ShouldBeEmpty();
        result.Title.ShouldContain("Authentication overhaul");
    }

    [Fact]
    public async Task OpenPlanAdo_DescendantWithSnapshot_CreatesNewPrWithFrontMatter()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100,
            planGenerations: new() { ["root"] = 2, ["5678"] = 4 });
        StubTwigShow(runner, 9999, "Detail");
        ado.ListPrs = new List<AdoPullRequest>();
        ado.CreatedPr = MakePr(
            id: 50,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/50",
            sourceRef: "refs/heads/plan/100-9999",
            targetRef: "refs/heads/plan/100-5678");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo,
                rootId: 100, itemId: 9999, parentItemId: 5678,
                ancestorIds: "5678,root", manifestPath: manifestPath));
        exit.ShouldBe(ExitCodes.Success);

        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.AncestorPlanGenerations.Count.ShouldBe(2);
        result.AncestorPlanGenerations["5678"].ShouldBe(4);
        result.AncestorPlanGenerations["root"].ShouldBe(2);

        ado.LastCreateDescription.ShouldNotBeNull();
        ado.LastCreateDescription!.ShouldStartWith("---\n");
        ado.LastCreateDescription.ShouldContain("ancestor_plan_generations:");
        ado.LastCreateDescription.ShouldContain("\"5678\": 4");
        ado.LastCreateDescription.ShouldContain("root: 2");
    }

    // ─── Reuse with matching snapshot ────────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_ExistingPrWithMatchingSnapshot_Reuses()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100,
            planGenerations: new() { ["root"] = 2 });
        StubTwigShow(runner, 5678, "Login");

        var existingBody = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 2\n---\n\nbody.";
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(
                id: 77,
                url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/77",
                sourceRef: "refs/heads/plan/100-5678",
                targetRef: "refs/heads/plan/100",
                description: existingBody)
        };
        ado.PollData = MakePollData(77, "plan/100-5678", "plan/100", existingBody);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo,
                rootId: 100, itemId: 5678, ancestorIds: "root", manifestPath: manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Created.ShouldBeFalse();
        result.Stale.ShouldBeFalse();
        result.PrNumber.ShouldBe(77);
        result.AncestorPlanGenerations["root"].ShouldBe(2);
        ado.CreatePrCallCount.ShouldBe(0);  // reuse: no Create call
    }

    // ─── Reuse with stale snapshot ───────────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_ExistingPrWithStaleSnapshot_RoutesStaleMetadata()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100,
            planGenerations: new() { ["root"] = 5 });
        StubTwigShow(runner, 5678, "Login");

        var existingBody = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 2\n---\n\nbody.";
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(
                id: 77,
                url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/77",
                sourceRef: "refs/heads/plan/100-5678",
                targetRef: "refs/heads/plan/100",
                description: existingBody)
        };
        ado.PollData = MakePollData(77, "plan/100-5678", "plan/100", existingBody);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo,
                rootId: 100, itemId: 5678, ancestorIds: "root", manifestPath: manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_metadata");
        result.Created.ShouldBeFalse();
        result.Stale.ShouldBeTrue();
        result.PrNumber.ShouldBe(77);
        // Reuse-stale path emits the embedded (stale) snapshot, not the current.
        result.AncestorPlanGenerations["root"].ShouldBe(2);
        ado.CreatePrCallCount.ShouldBe(0);
    }

    // ─── Reuse: source/target filter ─────────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_ListContainsUnrelatedPrs_FiltersBySourceAndTargetRef()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root plan");

        // Two unrelated active PRs that should NOT match the verb's
        // expected refs/heads/plan/100 → refs/heads/feature/100 pair.
        ado.ListPrs = new List<AdoPullRequest>
        {
            MakePr(id: 1, url: "u1",
                sourceRef: "refs/heads/different/branch",
                targetRef: "refs/heads/feature/100"),
            MakePr(id: 2, url: "u2",
                sourceRef: "refs/heads/plan/100",
                targetRef: "refs/heads/different/target"),
        };
        ado.CreatedPr = MakePr(
            id: 99,
            url: "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/99",
            sourceRef: "refs/heads/plan/100",
            targetRef: "refs/heads/feature/100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        var result = Parse(output);
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(99);
        ado.CreatePrCallCount.ShouldBe(1);
    }

    // ─── List/create wire-level failures ────────────────────────────────

    [Fact]
    public async Task OpenPlanAdo_ListReturnsNull_RoutesPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ListPrsReturnsNull = true;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task OpenPlanAdo_NoPat_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ThrowOnList = new InvalidOperationException("No PAT configured (set AZURE_DEVOPS_EXT_PAT).");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("no_pat");
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Fact]
    public async Task OpenPlanAdo_Http401_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ThrowOnList = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task OpenPlanAdo_Http403_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ThrowOnList = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task OpenPlanAdo_Http5xx_RoutesAdoFailed()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ThrowOnList = new HttpRequestException("server died", null, HttpStatusCode.BadGateway);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        Parse(output).ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task OpenPlanAdo_Timeout_RoutesAdoTimeout()
    {
        var (cmd, runner, ado) = CreateCommand();
        var manifestPath = SeedManifest(runner, rootId: 100);
        StubTwigShow(runner, 100, "Root");
        ado.ThrowOnList = new TimeoutException("attempts exhausted");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenPlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, manifestPath: manifestPath));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public List<AdoPullRequest>? ListPrs { get; set; }
        public bool ListPrsReturnsNull { get; set; }
        public Exception? ThrowOnList { get; set; }
        public AdoPullRequestPollData? PollData { get; set; }
        public AdoPullRequest? CreatedPr { get; set; }
        public int CreatePrCallCount { get; private set; }
        public string? LastCreateDescription { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null,
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
