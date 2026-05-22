using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class LockCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectLockAcquireEffects(LockMutationPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        return
        [
            CreateLockEffect(
                payload.Path,
                payload.RootId,
                ResourceIntent.EnsurePresent,
                payload.WasMutated ? ResourceMutation.CreatedNow : ResourceMutation.NoChangedExternalAlreadyPresent,
                payload.WasMutated,
                payload.ResultAction,
                payload.Reason),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectLockReleaseEffects(LockMutationPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        return
        [
            CreateLockEffect(
                payload.Path,
                payload.RootId,
                ResourceIntent.EnsureAbsent,
                payload.WasMutated ? ResourceMutation.DeletedNow : ResourceMutation.NoChangedAlreadySatisfied,
                true,
                payload.ResultAction,
                payload.Reason),
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectLockForceReleaseEffects(LockMutationPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        return
        [
            CreateLockEffect(
                payload.Path,
                payload.RootId,
                ResourceIntent.EnsureAbsent,
                payload.WasMutated ? ResourceMutation.DeletedNow : ResourceMutation.NoChangedAlreadySatisfied,
                true,
                payload.ResultAction,
                payload.Reason),
        ];
    }

    private static JournalResourceEffect CreateLockEffect(
        string path,
        int rootId,
        ResourceIntent intent,
        ResourceMutation mutation,
        bool polyphonyOwned,
        string resultAction,
        string? reason)
        => new()
        {
            Kind = ResourceKind.LockFile,
            Id = path,
            Intent = intent,
            Mutation = mutation,
            PolyphonyOwned = polyphonyOwned,
            ParentId = rootId > 0 ? $"workitem:{rootId}" : null,
            Attributes = JournalCommandSupport.CreateAttributes(
                ("root_id", rootId),
                ("result_action", resultAction),
                ("reason", reason)),
        };
}
