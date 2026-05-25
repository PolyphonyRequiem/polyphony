using System.Reflection;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournalEffectContractTests
{
    [Fact]
    public void JournaledActions_DeclareStaticResourceCapabilities()
    {
        var missingCapabilities = new[]
            {
                typeof(BranchCommands),
                typeof(PrCommands),
                typeof(ScopeCommands),
                typeof(RootCommands),
                typeof(LockCommands),
                typeof(ManifestCommands),
                typeof(WorktreeCommands),
                typeof(ResetCommands),
                typeof(PlanCommands),
            }
            .SelectMany(GetJournaledMethods)
            .Where(method => !method.GetCustomAttributes<MutatesResourceAttribute>().Any()
                && !method.GetCustomAttributes<MayObserveResourceAttribute>().Any())
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .OrderBy(name => name)
            .ToArray();

        missingCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public void EmittedEffects_RespectOwnershipInvariants()
    {
        var effects = new[]
        {
            InvokeSelector(typeof(BranchCommands), "SelectEnsurePlanEffects", new BranchEnsurePlanPayload
            {
                RootId = 100,
                WorkItemId = 200,
                ParentItemId = 100,
                IsRootPlan = false,
                BranchName = "plan/100-200",
                BaseBranch = "plan/100",
                Sha = "abc123",
                ResultAction = "created",
                Succeeded = true,
                WasMutated = true,
                WasCreated = true,
                WasPushed = true,
                BaseFetched = true,
            }),
            InvokeSelector(typeof(BranchCommands), "SelectEnsureFeatureEffects", new BranchEnsureFeaturePayload
            {
                RootId = 100,
                BranchName = "feature/100",
                BaseBranch = "main",
                WorktreePath = "C:\\repo",
                Sha = "abc124",
                ResultAction = "exists",
                Succeeded = true,
                WasMutated = false,
                WasCreated = false,
                WasPushed = false,
            }),
            InvokeSelector(typeof(PrCommands), "SelectCreateFeatureAdoEffects", new PrCreateFeatureAdoPayload
            {
                RootId = 100,
                Organization = "myorg",
                Project = "myproj",
                Repository = "myrepo",
                HeadBranch = "feature/100",
                BaseBranch = "main",
                RepoSlug = "myorg/myproj/myrepo",
                PrNumber = 60,
                PrUrl = "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/60",
                ResultAction = "created",
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(PrCommands), "SelectOpenPlanAdoEffects", new PrOpenPlanAdoPayload
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
                PrNumber = 61,
                PrUrl = "https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/61",
                ResultAction = "reused",
                Succeeded = true,
                WasMutated = false,
                Stale = false,
            }),
            InvokeSelector(typeof(WorktreeCommands), "SelectWorktreeAddEffects", new WorktreeAddPayload
            {
                Branch = "feature/100",
                Path = "C:/wt/100",
                GitRef = "origin/main",
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(WorktreeCommands), "SelectWorktreeCreateEffects", new WorktreeCreatePayload
            {
                RootId = 100,
                Branch = "plan/100-200",
                WorktreePath = "C:/wt/plan-100-200",
                Slug = "plan-100-200",
                Outcome = "idempotent",
                Succeeded = true,
                WasMutated = false,
                RootRoot = "C:/polyphony-runs/root-100",
            }),
            InvokeSelector(typeof(LockCommands), "SelectLockAcquireEffects", new LockMutationPayload
            {
                RootId = 100,
                Path = "C:/polyphony-runs/root-100/.lock",
                ResultAction = "acquired",
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(ManifestCommands), "SelectManifestMutationEffects", new ManifestMutationPayload
            {
                RootId = 100,
                Path = "C:/repo/.polyphony-config/run-manifest.json",
                PathSource = "explicit",
                ResultAction = "init_created",
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(PlanCommands), "SelectPlanSeedChildrenEffects", new PlanSeedChildrenPayload
            {
                WorkItemId = 100,
                ChildCount = 1,
                SeededItems = [new SeedReconciliation { WorkItemId = 200, ChildId = "task-1", MatchedBy = "created" }],
                ReusedItems = [],
                Errors = [],
                Warnings = [],
                PlannedTagMutated = true,
                PlannedTagAlreadyPresent = false,
                RootFacets = ["implementable"],
                FacetsTagMutated = true,
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(PlanCommands), "SelectPlanRebaseStaleDescendantEffects", new PlanRebaseStaleDescendantPayload
            {
                RootId = 100,
                ItemId = 200,
                ParentItemId = 100,
                PrNumber = 77,
                PrUrl = "https://github.com/owner/repo/pull/77",
                HeadBranch = "plan/100-200",
                ParentPlanBranch = "plan/100",
                Outcome = "rebased",
                OldHeadSha = "abc",
                NewHeadSha = "def",
                BodyUpdated = true,
                ManifestRecorded = true,
                ManifestPushed = true,
                CommentPosted = false,
                ConflictFiles = [],
                Warnings = [],
                Succeeded = true,
                WasMutated = true,
            }),
            InvokeSelector(typeof(PlanCommands), "SelectPlanRecreateStaleDescendantEffects", new PlanRecreateStaleDescendantPayload
            {
                RootId = 100,
                ItemId = 200,
                ParentItemId = 100,
                OldPrNumber = 77,
                OldPrUrl = "https://github.com/owner/repo/pull/77",
                OldHeadBranch = "plan/100-200",
                ParentPlanBranch = "plan/100",
                Outcome = "recreated",
                NewPrNumber = 78,
                NewPrUrl = "https://github.com/owner/repo/pull/78",
                NewHeadBranch = "plan/100-200-r2",
                OldPrClosed = true,
                OldBranchDeleted = true,
                NewBranchCreated = true,
                NewPrOpened = true,
                ManifestRecorded = true,
                ManifestPushed = true,
                Warnings = [],
                Succeeded = true,
                WasMutated = true,
            }),
        }.SelectMany(effectSet => effectSet).ToArray();

        effects.ShouldNotBeEmpty();

        foreach (var effect in effects.Where(effect => effect.Mutation == ResourceMutation.CreatedNow))
        {
            effect.PolyphonyOwned.ShouldBeTrue();
        }

        foreach (var effect in effects.Where(effect => effect.Mutation == ResourceMutation.NoChangedExternalAlreadyPresent))
        {
            effect.PolyphonyOwned.ShouldBeFalse();
        }

        foreach (var effect in effects.Where(effect => effect.Mutation == ResourceMutation.DeletedNow))
        {
            effect.PolyphonyOwned.ShouldBeTrue();
        }
    }

    private static IEnumerable<MethodInfo> GetJournaledMethods(Type commandType)
        => commandType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<JournaledActionAttribute>() is not null);

    private static IReadOnlyList<JournalResourceEffect> InvokeSelector(Type commandType, string methodName, object payload)
    {
        var method = commandType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.ShouldNotBeNull();
        return (IReadOnlyList<JournalResourceEffect>)method!.Invoke(null, [payload])!;
    }
}
