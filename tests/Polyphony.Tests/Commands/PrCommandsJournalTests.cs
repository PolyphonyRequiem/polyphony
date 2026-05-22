using System.Reflection;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class PrCommandsJournalTests : CommandTestBase
{
    private readonly string _scratchRoot = Path.Combine(AppContext.BaseDirectory, "pr-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdDirs = [];

    [Fact]
    public async Task CreateFeaturePr_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Add cool thing");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/42");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_create_feature_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("feature/100-x->main");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrCreateFeaturePrPayload);
        payload.ShouldNotBeNull();
        payload.WorkItemId.ShouldBe(100);
        payload.FeatureBranch.ShouldBe("feature/100-x");
        payload.PrNumber.ShouldBe(42);
        payload.WasMutated.ShouldBeTrue();
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitHubPr, "https://github.com/PolyphonyRequiem/polyphony/pull/42", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:100");
    }

    [Fact]
    public async Task OpenMergeGroupPr_ReusedPr_WritesNoOpJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListExisting(runner, 99, "https://github.com/PolyphonyRequiem/polyphony/pull/99", "mg/100_core");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.OpenMergeGroupPr(rootId: 100, mgPath: "core"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_open_mg_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("mg/100_core->feature/100");
        entry.Outcome.ShouldBe(JournalOutcome.NoOp);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrOpenMergeGroupPrPayload);
        payload.ShouldNotBeNull();
        payload.MergeGroupPath.ShouldBe("core");
        payload.PrNumber.ShouldBe(99);
        payload.WasMutated.ShouldBeFalse();
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitHubPr, "https://github.com/PolyphonyRequiem/polyphony/pull/99", ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, polyphonyOwned: false, parentId: "workitem:100");
    }

    [Fact]
    public async Task OpenMergeGroupPr_MissingHead_WritesFailureJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/mg/100_core", exists: false);

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.OpenMergeGroupPr(rootId: 100, mgPath: "core"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_open_mg_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Outcome.ShouldBe(JournalOutcome.Failure);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrOpenMergeGroupPrPayload);
        payload.ShouldNotBeNull();
        payload.Succeeded.ShouldBeFalse();
        payload.Error.ShouldNotBeNull();
        payload.Error.ShouldContain("head branch");
    }

    [Fact]
    public async Task MergeEvidencePr_GithubMerge_WritesSuccessJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        runner.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0,
            """{"number":42,"state":"MERGED","mergeCommit":{"oid":"deadbeef"},"headRefName":"evidence/100","headRefOid":"x"}""", ""));

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.MergeEvidencePr(
            prNumber: 42,
            prUrl: "https://github.com/PolyphonyRequiem/polyphony/pull/42",
            platform: "github",
            repository: "PolyphonyRequiem/polyphony"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_merge_evidence_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/42");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrMergeEvidencePrPayload);
        payload.ShouldNotBeNull();
        payload.PrNumber.ShouldBe(42);
        payload.MergeCommit.ShouldBe("deadbeef");
        payload.WasMutated.ShouldBeTrue();
        payload.AlreadyMerged.ShouldBeFalse();
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitHubPr, "https://github.com/PolyphonyRequiem/polyphony/pull/42", ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true);
    }

    [Fact]
    public void CreateFeatureAdo_Effects_SelectOwnedAdoPr()
    {
        var effects = InvokeSelector("SelectCreateFeatureAdoEffects", new PrCreateFeatureAdoPayload
        {
            RootId = 100,
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "feature/100",
            BaseBranch = "main",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 41,
            PrUrl = AdoPrUrl(41),
            Title = "Feature 100",
            ResultAction = "created",
            Succeeded = true,
            WasMutated = true,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(41), ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:100");
    }

    [Fact]
    public void OpenPlanPr_Effects_SelectOwnedGithubPr()
    {
        var effects = InvokeSelector("SelectOpenPlanPrEffects", new PrOpenPlanPrPayload
        {
            RootId = 100,
            ItemId = 200,
            ParentItemId = 100,
            ItemKey = "100/200",
            IsRootPlan = false,
            HeadBranch = "plan/100-200",
            BaseBranch = "plan/100",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrNumber = 42,
            PrUrl = GitHubPrUrl(42),
            Title = "Plan 200",
            ResultAction = "created",
            Succeeded = true,
            WasMutated = true,
            Stale = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(42), ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:200");
    }

    [Fact]
    public void OpenPlanAdo_Effects_SelectExternalAdoPr()
    {
        var effects = InvokeSelector("SelectOpenPlanAdoEffects", new PrOpenPlanAdoPayload
        {
            RootId = 100,
            ItemId = 200,
            ParentItemId = 100,
            ItemKey = "100/200",
            IsRootPlan = false,
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "plan/100-200",
            BaseBranch = "plan/100",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 43,
            PrUrl = AdoPrUrl(43),
            Title = "Plan 200",
            ResultAction = "reused",
            Succeeded = true,
            WasMutated = false,
            Stale = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(43), ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, polyphonyOwned: false, parentId: "workitem:200");
    }

    [Fact]
    public void OpenMergeGroupAdo_Effects_SelectOwnedAdoPr()
    {
        var effects = InvokeSelector("SelectOpenMergeGroupAdoEffects", new PrOpenMergeGroupAdoPayload
        {
            RootId = 100,
            MergeGroupPath = "core",
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "mg/100_core",
            BaseBranch = "feature/100",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 44,
            PrUrl = AdoPrUrl(44),
            Title = "MG core",
            ResultAction = "created",
            Succeeded = true,
            WasMutated = true,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(44), ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:100");
    }

    [Fact]
    public void OpenImplPr_Effects_SelectOwnedGithubPr()
    {
        var effects = InvokeSelector("SelectOpenImplPrEffects", new PrOpenImplPrPayload
        {
            RootId = 100,
            ItemId = 200,
            MergeGroupPath = "core",
            HeadBranch = "impl/100-200",
            BaseBranch = "mg/100_core",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrNumber = 45,
            PrUrl = GitHubPrUrl(45),
            Title = "Impl 200",
            ResultAction = "created",
            Succeeded = true,
            WasMutated = true,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(45), ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:200");
    }

    [Fact]
    public void OpenImplAdo_Effects_SelectExternalAdoPr()
    {
        var effects = InvokeSelector("SelectOpenImplAdoEffects", new PrOpenImplAdoPayload
        {
            RootId = 100,
            ItemId = 200,
            MergeGroupPath = "core",
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "impl/100-200",
            BaseBranch = "mg/100_core",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 46,
            PrUrl = AdoPrUrl(46),
            Title = "Impl 200",
            ResultAction = "reused",
            Succeeded = true,
            WasMutated = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(46), ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, polyphonyOwned: false, parentId: "workitem:200");
    }

    [Fact]
    public void OpenEvidencePr_Effects_SelectOwnedGithubPr()
    {
        var effects = InvokeSelector("SelectOpenEvidencePrEffects", new PrOpenEvidencePrPayload
        {
            RootId = 100,
            WorkItemId = 300,
            HeadBranch = "evidence/100-300",
            BaseBranch = "impl/100-300",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrNumber = 47,
            PrUrl = GitHubPrUrl(47),
            Title = "Evidence 300",
            ResultAction = "created",
            Succeeded = true,
            WasMutated = true,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(47), ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: "workitem:300");
    }

    [Fact]
    public void OpenEvidenceAdo_Effects_SelectExternalAdoPr()
    {
        var effects = InvokeSelector("SelectOpenEvidenceAdoEffects", new PrOpenEvidenceAdoPayload
        {
            RootId = 100,
            WorkItemId = 300,
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "evidence/100-300",
            BaseBranch = "impl/100-300",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 48,
            PrUrl = AdoPrUrl(48),
            Title = "Evidence 300",
            ResultAction = "reused",
            Succeeded = true,
            WasMutated = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(48), ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, polyphonyOwned: false, parentId: "workitem:300");
    }

    [Fact]
    public void MergePlanPr_Effects_SelectPrAndBaseBranch()
    {
        var effects = InvokeSelector("SelectMergePlanPrEffects", new PrMergePlanPrPayload
        {
            RootId = 100,
            ItemId = 200,
            ParentItemId = 100,
            PrNumber = 49,
            ItemKey = "100/200",
            IsRootPlan = false,
            HeadBranch = "plan/100-200",
            BaseBranch = "plan/100",
            ManifestBranch = "feature/100",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrUrl = GitHubPrUrl(49),
            MergeCommit = "abc123",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        });

        effects.Count.ShouldBe(2);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(49), ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true, parentId: "workitem:200");
        AssertEffect(effects[1], ResourceKind.GitBranch, "plan/100", ResourceIntent.AdvancePointer, ResourceMutation.Changed, polyphonyOwned: false);
    }

    [Fact]
    public void MergePlanAdo_Effects_SelectPrAndBaseBranch()
    {
        var effects = InvokeSelector("SelectMergePlanAdoEffects", new PrMergePlanAdoPayload
        {
            RootId = 100,
            ItemId = 200,
            ParentItemId = 100,
            PrNumber = 50,
            ItemKey = "100/200",
            IsRootPlan = false,
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "plan/100-200",
            BaseBranch = "plan/100",
            ManifestBranch = "feature/100",
            RepoSlug = "myorg/myproj/myrepo",
            PrUrl = AdoPrUrl(50),
            MergeCommit = "abc124",
            ResultAction = "already_merged",
            Succeeded = true,
            WasMutated = false,
            AlreadyMerged = true,
        });

        effects.Count.ShouldBe(2);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(50), ResourceIntent.SetState, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false, parentId: "workitem:200");
        AssertEffect(effects[1], ResourceKind.GitBranch, "plan/100", ResourceIntent.AdvancePointer, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
    }

    [Fact]
    public void MergeMergeGroupPr_Effects_SelectPrBaseAndHeadDeletion()
    {
        var effects = InvokeSelector("SelectMergeMergeGroupPrEffects", new PrMergeMergeGroupPrPayload
        {
            RootId = 100,
            MergeGroupPath = "core",
            HeadBranch = "mg/100_core",
            BaseBranch = "feature/100",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrNumber = 51,
            PrUrl = GitHubPrUrl(51),
            Method = "squash",
            DeleteBranch = true,
            MergeCommit = "abc125",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        });

        effects.Count.ShouldBe(3);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(51), ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true, parentId: "workitem:100");
        AssertEffect(effects[1], ResourceKind.GitBranch, "feature/100", ResourceIntent.AdvancePointer, ResourceMutation.Changed, polyphonyOwned: false);
        AssertEffect(effects[2], ResourceKind.GitBranch, "mg/100_core", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, polyphonyOwned: false);
    }

    [Fact]
    public void MergeMergeGroupAdo_Effects_SelectPrBaseAndHeadDeletion()
    {
        var effects = InvokeSelector("SelectMergeMergeGroupAdoEffects", new PrMergeMergeGroupAdoPayload
        {
            RootId = 100,
            MergeGroupPath = "core",
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "mg/100_core",
            BaseBranch = "feature/100",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 52,
            PrUrl = AdoPrUrl(52),
            Method = "squash",
            DeleteBranch = true,
            MergeCommit = "abc126",
            ResultAction = "already_merged",
            Succeeded = true,
            WasMutated = false,
            AlreadyMerged = true,
        });

        effects.Count.ShouldBe(3);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(52), ResourceIntent.SetState, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false, parentId: "workitem:100");
        AssertEffect(effects[1], ResourceKind.GitBranch, "feature/100", ResourceIntent.AdvancePointer, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
        AssertEffect(effects[2], ResourceKind.GitBranch, "mg/100_core", ResourceIntent.EnsureAbsent, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
    }

    [Fact]
    public void MergeImplPr_Effects_SelectPrBaseAndHeadDeletion()
    {
        var effects = InvokeSelector("SelectMergeImplPrEffects", new PrMergeImplPrPayload
        {
            RootId = 100,
            ItemId = 200,
            MergeGroupPath = "core",
            HeadBranch = "impl/100-200",
            BaseBranch = "mg/100_core",
            RepoSlug = "PolyphonyRequiem/polyphony",
            PrNumber = 53,
            PrUrl = GitHubPrUrl(53),
            Method = "merge",
            DeleteBranch = true,
            MergeCommit = "abc127",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        });

        effects.Count.ShouldBe(3);
        AssertEffect(effects[0], ResourceKind.GitHubPr, GitHubPrUrl(53), ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true, parentId: "workitem:200");
        AssertEffect(effects[1], ResourceKind.GitBranch, "mg/100_core", ResourceIntent.AdvancePointer, ResourceMutation.Changed, polyphonyOwned: false);
        AssertEffect(effects[2], ResourceKind.GitBranch, "impl/100-200", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, polyphonyOwned: false);
    }

    [Fact]
    public void MergeImplAdo_Effects_SelectPrBaseAndHeadDeletion()
    {
        var effects = InvokeSelector("SelectMergeImplAdoEffects", new PrMergeImplAdoPayload
        {
            RootId = 100,
            ItemId = 200,
            MergeGroupPath = "core",
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "impl/100-200",
            BaseBranch = "mg/100_core",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 54,
            PrUrl = AdoPrUrl(54),
            Method = "merge",
            DeleteBranch = true,
            MergeCommit = "abc128",
            ResultAction = "already_merged",
            Succeeded = true,
            WasMutated = false,
            AlreadyMerged = true,
        });

        effects.Count.ShouldBe(3);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(54), ResourceIntent.SetState, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false, parentId: "workitem:200");
        AssertEffect(effects[1], ResourceKind.GitBranch, "mg/100_core", ResourceIntent.AdvancePointer, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
        AssertEffect(effects[2], ResourceKind.GitBranch, "impl/100-200", ResourceIntent.EnsureAbsent, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
    }

    [Fact]
    public void MergeEvidenceAdo_Effects_SelectAdoPrOnly()
    {
        var effects = InvokeSelector("SelectMergeEvidenceAdoEffects", new PrMergeEvidenceAdoPayload
        {
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 55,
            PrUrl = AdoPrUrl(55),
            MergeCommit = "abc129",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(55), ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true);
    }

    [Fact]
    public void MergeFeatureAdo_Effects_SelectPrBaseAndHeadDeletion()
    {
        var effects = InvokeSelector("SelectMergeFeatureAdoEffects", new PrMergeFeatureAdoPayload
        {
            RootId = 100,
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            HeadBranch = "feature/100",
            BaseBranch = "main",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 56,
            PrUrl = AdoPrUrl(56),
            Method = "squash",
            DeleteBranch = true,
            MergeCommit = "abc130",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        });

        effects.Count.ShouldBe(3);
        AssertEffect(effects[0], ResourceKind.AdoPr, AdoPrUrl(56), ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true, parentId: "workitem:100");
        AssertEffect(effects[1], ResourceKind.GitBranch, "main", ResourceIntent.AdvancePointer, ResourceMutation.Changed, polyphonyOwned: false);
        AssertEffect(effects[2], ResourceKind.GitBranch, "feature/100", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, polyphonyOwned: false);
    }

    [Fact]
    public void PostCommentAdo_Effects_SelectCreatedComment()
    {
        var effects = InvokeSelector("SelectPostCommentAdoEffects", new PrPostCommentAdoPayload
        {
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 57,
            PrUrl = AdoPrUrl(57),
            Body = "Looks good.",
            ThreadId = 12,
            CommentId = 34,
            ResultAction = "posted",
            Succeeded = true,
            WasMutated = true,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPrComment, "34", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true, parentId: AdoPrUrl(57));
    }

    [Fact]
    public void VoteAdo_Effects_SelectReviewerVote()
    {
        var effects = InvokeSelector("SelectVoteAdoEffects", new PrVoteAdoPayload
        {
            Organization = "myorg",
            Project = "myproj",
            Repository = "myrepo",
            RepoSlug = "myorg/myproj/myrepo",
            PrNumber = 58,
            PrUrl = AdoPrUrl(58),
            ReviewerId = "reviewer-1",
            Vote = "approve",
            VoteValue = 10,
            ResultAction = "already_set",
            Succeeded = true,
            WasMutated = false,
        });

        effects.Count.ShouldBe(1);
        AssertEffect(effects[0], ResourceKind.AdoPrVote, "reviewer-1", ResourceIntent.SetState, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: true, parentId: AdoPrUrl(58));
    }

    private static IReadOnlyList<JournalResourceEffect> InvokeSelector(string methodName, object payload)
    {
        var method = typeof(PrCommands).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return (IReadOnlyList<JournalResourceEffect>)method!.Invoke(null, [payload])!;
    }

    private static void AssertEffect(
        JournalResourceEffect effect,
        string kind,
        string id,
        ResourceIntent intent,
        ResourceMutation mutation,
        bool polyphonyOwned,
        string? parentId = null)
    {
        effect.Kind.ShouldBe(kind);
        effect.Id.ShouldBe(id);
        effect.Intent.ShouldBe(intent);
        effect.Mutation.ShouldBe(mutation);
        effect.PolyphonyOwned.ShouldBe(polyphonyOwned);
        effect.ParentId.ShouldBe(parentId);
    }

    private static string GitHubPrUrl(int number) => $"https://github.com/PolyphonyRequiem/polyphony/pull/{number}";

    private static string AdoPrUrl(int number) => $"https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/{number}";

    private (PrCommands Command, FakeProcessRunner Runner, JournalStore Store) CreateCommand(ProcessConfig? cfg = null, string runId = "run-pr-journal")
    {
        var scratchDir = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratchDir, ".polyphony-state"));
        _createdDirs.Add(scratchDir);

        var store = new JournalStore(Path.Combine(scratchDir, ".polyphony-state", "journal.db"));
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git,
            gh,
            twig,
            Repository,
            cfg ?? Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new RunContext(runId),
            new JournaledActionDecorator(store));
        return (cmd, runner, store);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemoteHas(FakeProcessRunner runner, string remote, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", remote, pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubTwigShowTree(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--tree", "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url, string headRefName)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"{{headRefName}}"}]""", ""));

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    public override void Dispose()
    {
        base.Dispose();
        foreach (var dir in _createdDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
