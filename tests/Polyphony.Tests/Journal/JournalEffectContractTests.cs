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
    public void JournaledBranchAndPrActions_DeclareStaticResourceCapabilities()
    {
        var missingCapabilities = GetJournaledMethods(typeof(BranchCommands))
            .Concat(GetJournaledMethods(typeof(PrCommands)))
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
