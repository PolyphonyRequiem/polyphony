using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class WorktreeCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectWorktreeAddEffects(WorktreeAddPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        return [CreateWorktreeEffect(payload.Path, payload.Branch, ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, true, ("git_ref", payload.GitRef))];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectWorktreeRemoveEffects(WorktreeRemovePayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        return [CreateWorktreeEffect(payload.Path, null, ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, true, ("force", payload.Force))];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectWorktreeCreateEffects(WorktreeCreatePayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.WorktreePath))
        {
            return [];
        }

        return payload.Outcome switch
        {
            "created" or "attached" => [CreateWorktreeEffect(payload.WorktreePath, payload.Branch, ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, true, ("root_id", payload.RootId), ("slug", payload.Slug), ("ref", payload.Ref), ("root_root", payload.RootRoot), ("outcome", payload.Outcome))],
            "idempotent" => [CreateWorktreeEffect(payload.WorktreePath, payload.Branch, ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, false, ("root_id", payload.RootId), ("slug", payload.Slug), ("root_root", payload.RootRoot), ("outcome", payload.Outcome))],
            _ => [],
        };
    }

    private static IReadOnlyList<JournalResourceEffect> SelectWorktreeInitRootEffects(WorktreeInitRootPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.WorktreePath))
        {
            return [];
        }

        return payload.Outcome switch
        {
            "created" or "attached" => [CreateWorktreeEffect(payload.WorktreePath, payload.Branch, ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, true, ("root_id", payload.RootId), ("root_root", payload.RootRoot), ("runs_root", payload.RunsRoot), ("main_worktree_path", payload.MainWorktreePath), ("outcome", payload.Outcome))],
            "idempotent" => [CreateWorktreeEffect(payload.WorktreePath, payload.Branch, ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, false, ("root_id", payload.RootId), ("root_root", payload.RootRoot), ("runs_root", payload.RunsRoot), ("main_worktree_path", payload.MainWorktreePath), ("outcome", payload.Outcome))],
            _ => [],
        };
    }

    private static IReadOnlyList<JournalResourceEffect> SelectWorktreeGcEffects(WorktreeGcPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.DryRun)
        {
            return [];
        }

        return payload.Candidates
            .Where(candidate => candidate.Removed)
            .Select(candidate => CreateWorktreeEffect(candidate.Path, candidate.Branch, ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, true, ("root_id", payload.Root), ("reason", candidate.Reason), ("runs_root", payload.RunsRoot)))
            .ToArray();
    }

    private static JournalResourceEffect CreateWorktreeEffect(
        string path,
        string? branch,
        ResourceIntent intent,
        ResourceMutation mutation,
        bool polyphonyOwned,
        params (string Name, object? Value)[] attributes)
    {
        var jsonAttributes = JournalCommandSupport.CreateAttributes(("branch", branch));
        JournalCommandSupport.AppendAttributes(jsonAttributes, attributes);

        return new JournalResourceEffect
        {
            Kind = ResourceKind.GitWorktree,
            Id = path,
            Intent = intent,
            Mutation = mutation,
            PolyphonyOwned = polyphonyOwned,
            Attributes = jsonAttributes,
        };
    }
}
