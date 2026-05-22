using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class ScopeCommands
{
    internal static IReadOnlyList<JournalResourceEffect> SelectTagMutationEffects(TagMutationPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Tag) || payload.WorkItemId <= 0)
        {
            return [];
        }

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoWorkItemTag,
                Id = $"{payload.WorkItemId}:{payload.Tag}",
                Intent = payload.EnsurePresent ? ResourceIntent.EnsurePresent : ResourceIntent.EnsureAbsent,
                Mutation = payload.WasMutated
                    ? (payload.EnsurePresent ? ResourceMutation.CreatedNow : ResourceMutation.DeletedNow)
                    : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = JournalCommandSupport.WorkItemTarget(payload.WorkItemId),
                Attributes = JournalCommandSupport.CreateAttributes(
                    ("tag", payload.Tag),
                    ("tags_before", string.Join(';', payload.TagsBefore)),
                    ("tags_after", string.Join(';', payload.TagsAfter))),
            },
        ];
    }
}
