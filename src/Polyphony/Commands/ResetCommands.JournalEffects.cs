using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class ResetCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectResetPrsEffects(ResetPrsPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun)
        {
            return [];
        }

        return payload.AbandonedPrs
            .Select(pr => new JournalResourceEffect
            {
                Kind = pr.Url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? ResourceKind.AdoPr : ResourceKind.GitHubPr,
                Id = pr.Url,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = ResourceMutation.DeletedNow,
                PolyphonyOwned = true,
                Platform = pr.Url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? "ado" : "github",
                ParentId = $"workitem:{payload.Root}",
                Attributes = JournalCommandSupport.CreateAttributes(("pr_number", pr.Number), ("head_branch", pr.HeadBranch), ("repo_slug", payload.RepoSlug)),
            })
            .ToArray();
    }

    private static IReadOnlyList<JournalResourceEffect> SelectResetWorktreesEffects(ResetWorktreesPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun)
        {
            return [];
        }

        return payload.RemovedWorktrees
            .Select(worktree => new JournalResourceEffect
            {
                Kind = ResourceKind.GitWorktree,
                Id = worktree.Path,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = ResourceMutation.DeletedNow,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.Root}",
                Attributes = JournalCommandSupport.CreateAttributes(("branch", worktree.Branch), ("root_runs_root", payload.RootRunsRoot)),
            })
            .ToArray();
    }

    private static IReadOnlyList<JournalResourceEffect> SelectResetBranchesEffects(ResetBranchesPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun)
        {
            return [];
        }

        return payload.DeletedBranches
            .Select(branch => new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = branch.Branch,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = ResourceMutation.DeletedNow,
                PolyphonyOwned = true,
                ParentId = $"workitem:{payload.Root}",
                Attributes = JournalCommandSupport.CreateAttributes(("deleted_local", branch.DeletedLocal), ("deleted_remote", branch.DeletedRemote)),
            })
            .ToArray();
    }

    private static IReadOnlyList<JournalResourceEffect> SelectResetFacetsEffects(ResetFacetsPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun)
        {
            return [];
        }

        var effects = new List<JournalResourceEffect>();
        foreach (var item in payload.Items.Where(item => item.Verified == true))
        {
            foreach (var facetTag in item.FacetTagsRemoved)
            {
                effects.Add(new JournalResourceEffect
                {
                    Kind = ResourceKind.AdoWorkItemTag,
                    Id = $"{item.WorkItemId}:{facetTag}",
                    Intent = ResourceIntent.EnsureAbsent,
                    Mutation = ResourceMutation.DeletedNow,
                    PolyphonyOwned = true,
                    Platform = "ado",
                    ParentId = $"workitem:{item.WorkItemId}",
                    Attributes = JournalCommandSupport.CreateAttributes(("root_id", payload.Root), ("tag", facetTag)),
                });
            }

            if (item.PlannedTagRemoved)
            {
                effects.Add(new JournalResourceEffect
                {
                    Kind = ResourceKind.AdoWorkItemTag,
                    Id = $"{item.WorkItemId}:polyphony:planned",
                    Intent = ResourceIntent.EnsureAbsent,
                    Mutation = ResourceMutation.DeletedNow,
                    PolyphonyOwned = true,
                    Platform = "ado",
                    ParentId = $"workitem:{item.WorkItemId}",
                    Attributes = JournalCommandSupport.CreateAttributes(("root_id", payload.Root), ("tag", "polyphony:planned")),
                });
            }
        }

        return effects;
    }

    private static IReadOnlyList<JournalResourceEffect> SelectResetStateEffects(ResetStatePayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun || string.IsNullOrWhiteSpace(payload.NewWatermark))
        {
            return [];
        }

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemTag,
                Id = $"{payload.Root}:polyphony:run-started-at={payload.NewWatermark}",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = $"workitem:{payload.Root}",
                Attributes = JournalCommandSupport.CreateAttributes(("previous_watermark", payload.PreviousWatermark), ("removed_duplicate_tags", payload.RemovedDuplicateTags)),
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectResetRootEffects(ResetRootPayload? payload)
    {
        _ = payload;
        return [];
    }
}
