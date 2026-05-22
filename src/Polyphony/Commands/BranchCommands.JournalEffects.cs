using System.Globalization;
using System.Text.Json.Nodes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectEnsureFeatureEffects(BranchEnsureFeaturePayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.BranchName))
        {
            return [];
        }

        return
        [
            CreateBranchEnsureEffect(
                payload.BranchName,
                payload.BaseBranch,
                payload.WasCreated,
                payload.Sha,
                payload.ResultAction,
                payload.RootId,
                payload.WorktreePath,
                ("was_pushed", payload.WasPushed)),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectEnsureImplEffects(BranchEnsureImplPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.BranchName))
        {
            return [];
        }

        return
        [
            CreateBranchEnsureEffect(
                payload.BranchName,
                payload.BaseBranch,
                payload.WasCreated,
                payload.Sha,
                payload.ResultAction,
                payload.RootId,
                null,
                ("work_item_id", payload.WorkItemId),
                ("merge_group_path", payload.MergeGroupPath),
                ("was_pushed", payload.WasPushed),
                ("base_fetched", payload.BaseFetched)),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectEnsurePlanEffects(BranchEnsurePlanPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.BranchName))
        {
            return [];
        }

        return
        [
            CreateBranchEnsureEffect(
                payload.BranchName,
                payload.BaseBranch,
                payload.WasCreated,
                payload.Sha,
                payload.ResultAction,
                payload.RootId,
                null,
                ("work_item_id", payload.WorkItemId),
                ("parent_item_id", payload.ParentItemId),
                ("is_root_plan", payload.IsRootPlan),
                ("was_pushed", payload.WasPushed),
                ("base_fetched", payload.BaseFetched)),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectEnsureMergeGroupEffects(BranchEnsureMergeGroupPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.BranchName))
        {
            return [];
        }

        return
        [
            CreateBranchEnsureEffect(
                payload.BranchName,
                payload.BaseBranch,
                payload.WasCreated,
                payload.Sha,
                payload.ResultAction,
                payload.RootId,
                null,
                ("merge_group_path", payload.MergeGroupPath),
                ("depth", payload.Depth),
                ("was_pushed", payload.WasPushed),
                ("base_fetched", payload.BaseFetched)),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectEnsureEvidenceEffects(BranchEnsureEvidenceBranchPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.BranchName))
        {
            return [];
        }

        return
        [
            CreateBranchEnsureEffect(
                payload.BranchName,
                payload.BaseBranch,
                payload.WasCreated,
                payload.Sha,
                payload.ResultAction,
                payload.RootId,
                null,
                ("work_item_id", payload.WorkItemId),
                ("orphan", payload.Orphan),
                ("from_ref", payload.FromRef),
                ("was_pushed", payload.WasPushed),
                ("base_fetched", payload.BaseFetched)),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectMarkImplMergedEffects(BranchMarkImplMergedPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Tag))
        {
            return [];
        }

        return [CreateImplMergedTagEffect(payload.WorkItemId, payload.Tag, payload.MergeGroupPath, ensurePresent: true, payload.WasMutated)];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectClearImplMergedEffects(BranchClearImplMergedPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Tag))
        {
            return [];
        }

        return [CreateImplMergedTagEffect(payload.WorkItemId, payload.Tag, payload.MergeGroupPath, ensurePresent: false, payload.WasMutated)];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectNextImplEffects(BranchNextImplPayload? payload)
    {
        if (payload is null
            || !payload.Succeeded
            || payload.SelectedWorkItemId is not { } selectedWorkItemId)
        {
            return [];
        }

        var workItemId = WorkItemJournalTarget(selectedWorkItemId);
        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemState,
                Id = workItemId,
                Intent = ResourceIntent.SetState,
                Mutation = payload.WasMutated ? ResourceMutation.Changed : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = payload.WasMutated,
                Platform = "ado",
                Attributes = CreateAttributes(
                    ("root_id", payload.RootId),
                    ("container_id", payload.ContainerId),
                    ("merge_group_name", payload.MergeGroupName),
                    ("target_state", payload.TargetState),
                    ("branch_name", payload.BranchName),
                    ("ado_workspace", payload.AdoWorkspace)),
            },
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItem,
                Id = workItemId,
                Intent = ResourceIntent.Observe,
                Mutation = ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = false,
                Platform = "ado",
                Attributes = CreateAttributes(
                    ("root_id", payload.RootId),
                    ("container_id", payload.ContainerId),
                    ("merge_group_name", payload.MergeGroupName),
                    ("branch_name", payload.BranchName),
                    ("ado_workspace", payload.AdoWorkspace)),
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectCloseScopeEffects(BranchCloseScopePayload? payload)
    {
        if (payload is null || !payload.Succeeded)
        {
            return [];
        }

        return payload.ClosedItems
            .Select(item => new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemState,
                Id = WorkItemJournalTarget(item.Id),
                Intent = ResourceIntent.SetState,
                Mutation = ResourceMutation.Changed,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = WorkItemJournalTarget(payload.RootWorkItemId),
                Attributes = CreateAttributes(
                    ("merge_group_name", payload.MergeGroupName),
                    ("pr_number", payload.PrNumber),
                    ("target_state", item.TargetState),
                    ("title", item.Title),
                    ("ado_workspace", payload.AdoWorkspace)),
            })
            .ToArray();
    }

    private static JournalResourceEffect CreateBranchEnsureEffect(
        string branchName,
        string baseBranch,
        bool wasCreated,
        string? sha,
        string resultAction,
        int? rootId,
        string? worktreePath = null,
        params (string Name, object? Value)[] extraAttributes)
    {
        var attributes = CreateAttributes(
            ("base_branch", baseBranch),
            ("sha", sha),
            ("result_action", resultAction),
            ("worktree_path", worktreePath));
        AppendAttributes(attributes, extraAttributes);

        return new JournalResourceEffect
        {
            Kind = ResourceKind.GitBranch,
            Id = branchName,
            Intent = ResourceIntent.EnsurePresent,
            Mutation = wasCreated ? ResourceMutation.CreatedNow : ResourceMutation.NoChangedExternalAlreadyPresent,
            PolyphonyOwned = wasCreated,
            ParentId = rootId is > 0 ? WorkItemJournalTarget(rootId.Value) : null,
            Attributes = attributes.Count == 0 ? null : attributes,
        };
    }

    private static JournalResourceEffect CreateImplMergedTagEffect(
        int workItemId,
        string tag,
        string mergeGroupPath,
        bool ensurePresent,
        bool wasMutated)
        => new()
        {
            Kind = ResourceKind.AdoWorkItemTag,
            Id = $"{workItemId.ToString(CultureInfo.InvariantCulture)}:{tag}",
            Intent = ensurePresent ? ResourceIntent.EnsurePresent : ResourceIntent.EnsureAbsent,
            Mutation = wasMutated
                ? (ensurePresent ? ResourceMutation.CreatedNow : ResourceMutation.DeletedNow)
                : ResourceMutation.NoChangedAlreadySatisfied,
            PolyphonyOwned = true,
            Platform = "ado",
            ParentId = WorkItemJournalTarget(workItemId),
            Attributes = CreateAttributes(
                ("tag", tag),
                ("merge_group_path", mergeGroupPath)),
        };

    private static JsonObject CreateAttributes(params (string Name, object? Value)[] attributes)
    {
        var result = new JsonObject();
        AppendAttributes(result, attributes);
        return result;
    }

    private static void AppendAttributes(JsonObject attributes, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            attributes[name] = JsonValue.Create(value);
        }
    }
}
