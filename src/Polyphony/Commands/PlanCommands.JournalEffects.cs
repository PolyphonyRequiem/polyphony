using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class PlanCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectPlanWritePlanEffects(PlanWritePlanPayload? payload)
    {
        if (payload is null || !payload.Succeeded)
        {
            return [];
        }

        var effects = new List<JournalResourceEffect>();
        if (!string.IsNullOrWhiteSpace(payload.Path))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.PlanFile,
                Id = payload.Path,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = payload.ContentChanged
                    ? (payload.PathExistedBefore ? ResourceMutation.Changed : ResourceMutation.CreatedNow)
                    : ResourceMutation.NoChangedExternalAlreadyPresent,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.ItemId}",
                Attributes = JournalCommandSupport.CreateAttributes(("content_sha256", payload.ContentSha256)),
            });
        }

        if (!payload.ChildrenSkipped && !string.IsNullOrWhiteSpace(payload.ChildrenPath))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.PlanFile,
                Id = payload.ChildrenPath,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = payload.ChildrenChanged
                    ? (payload.ChildrenPathExistedBefore ? ResourceMutation.Changed : ResourceMutation.CreatedNow)
                    : ResourceMutation.NoChangedExternalAlreadyPresent,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.ItemId}",
                Attributes = JournalCommandSupport.CreateAttributes(("children_sha256", payload.ChildrenSha256), ("sidecar", true)),
            });
        }

        return effects;
    }

    private static IReadOnlyList<JournalResourceEffect> SelectPlanCommitAndPushEffects(PlanCommitAndPushPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Branch))
        {
            return [];
        }

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = payload.Branch,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = payload.Pushed ? ResourceMutation.Changed : ResourceMutation.NoChangedExternalAlreadyPresent,
                PolyphonyOwned = true,
                Attributes = JournalCommandSupport.CreateAttributes(("commit_sha", payload.CommitSha), ("files_staged", payload.FilesStaged), ("paths", string.Join(';', payload.Paths)), ("no_op_reason", payload.NoOpReason)),
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectPlanSeedChildrenEffects(PlanSeedChildrenPayload? payload)
    {
        if (payload is null || !payload.Succeeded)
        {
            return [];
        }

        var effects = new List<JournalResourceEffect>();
        foreach (var seeded in payload.SeededItems)
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItem,
                Id = $"workitem:{seeded.WorkItemId}",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = $"workitem:{payload.WorkItemId}",
                Attributes = JournalCommandSupport.CreateAttributes(("child_id", seeded.ChildId), ("matched_by", seeded.MatchedBy)),
            });
        }

        if (payload.PlannedTagMutated)
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemTag,
                Id = $"{payload.WorkItemId}:polyphony:planned",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = $"workitem:{payload.WorkItemId}",
            });
        }

        if (payload.FacetsTagMutated && payload.RootFacets.Count > 0)
        {
            var facetsTag = $"polyphony:facets={string.Join(',', payload.RootFacets)}";
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemTag,
                Id = $"{payload.WorkItemId}:{facetsTag}",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = $"workitem:{payload.WorkItemId}",
            });
        }

        return effects;
    }

    private static IReadOnlyList<JournalResourceEffect> SelectPlanRebaseStaleDescendantEffects(PlanRebaseStaleDescendantPayload? payload)
    {
        if (payload is null || !payload.Succeeded)
        {
            return [];
        }

        var effects = new List<JournalResourceEffect>();
        if (!string.IsNullOrWhiteSpace(payload.HeadBranch) && !string.IsNullOrWhiteSpace(payload.NewHeadSha))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = payload.HeadBranch,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.RootId}",
                Attributes = JournalCommandSupport.CreateAttributes(("old_head_sha", payload.OldHeadSha), ("new_head_sha", payload.NewHeadSha), ("parent_plan_branch", payload.ParentPlanBranch)),
            });
        }

        if (payload.BodyUpdated && !string.IsNullOrWhiteSpace(payload.PrUrl))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = payload.PrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? ResourceKind.AdoPr : ResourceKind.GitHubPr,
                Id = payload.PrUrl,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                Platform = payload.PrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? "ado" : "github",
                ParentId = $"workitem:{payload.ItemId}",
                Attributes = JournalCommandSupport.CreateAttributes(("pr_number", payload.PrNumber), ("outcome", payload.Outcome)),
            });
        }

        if (payload.ManifestRecorded || payload.ManifestPushed)
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.ManifestFile,
                Id = $"manifest:{payload.RootId}",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.RootId}",
            });
        }

        return effects;
    }

    private static IReadOnlyList<JournalResourceEffect> SelectPlanRecreateStaleDescendantEffects(PlanRecreateStaleDescendantPayload? payload)
    {
        if (payload is null || !payload.Succeeded)
        {
            return [];
        }

        var effects = new List<JournalResourceEffect>();
        if (payload.OldPrClosed && !string.IsNullOrWhiteSpace(payload.OldPrUrl))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = payload.OldPrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? ResourceKind.AdoPr : ResourceKind.GitHubPr,
                Id = payload.OldPrUrl,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = ResourceMutation.DeletedNow,
                PolyphonyOwned = true,
                Platform = payload.OldPrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? "ado" : "github",
                ParentId = $"workitem:{payload.ItemId}",
            });
        }

        if (payload.OldBranchDeleted && !string.IsNullOrWhiteSpace(payload.OldHeadBranch))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = payload.OldHeadBranch,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = ResourceMutation.DeletedNow,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.RootId}",
            });
        }

        if (payload.NewBranchCreated && !string.IsNullOrWhiteSpace(payload.NewHeadBranch))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = payload.NewHeadBranch,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.RootId}",
            });
        }

        if (payload.NewPrOpened && !string.IsNullOrWhiteSpace(payload.NewPrUrl))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = payload.NewPrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? ResourceKind.AdoPr : ResourceKind.GitHubPr,
                Id = payload.NewPrUrl,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
                Platform = payload.NewPrUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? "ado" : "github",
                ParentId = $"workitem:{payload.ItemId}",
            });
        }

        if (payload.ManifestRecorded || payload.ManifestPushed)
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.ManifestFile,
                Id = $"manifest:{payload.RootId}",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.RootId}",
            });
        }

        return effects;
    }
}
