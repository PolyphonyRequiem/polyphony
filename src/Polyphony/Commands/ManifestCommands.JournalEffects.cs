using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class ManifestCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectManifestMutationEffects(ManifestMutationPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.Path))
        {
            return [];
        }

        var mutation = payload.ResultAction switch
        {
            "init_created" => ResourceMutation.CreatedNow,
            _ when !payload.WasMutated => ResourceMutation.NoChangedAlreadySatisfied,
            _ => ResourceMutation.Changed,
        };

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.ManifestFile,
                Id = payload.Path,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = mutation,
                PolyphonyOwned = true,
                ParentId = payload.RootId > 0 ? $"workitem:{payload.RootId}" : null,
                Attributes = JournalCommandSupport.CreateAttributes(
                    ("path_source", payload.PathSource),
                    ("result_action", payload.ResultAction),
                    ("platform_project", payload.PlatformProject),
                    ("topology_hash", payload.TopologyHash),
                    ("branch", payload.Branch),
                    ("onto", payload.Onto),
                    ("reason", payload.Reason),
                    ("commit", payload.Commit),
                    ("gate", payload.Gate),
                    ("approved_by", payload.ApprovedBy),
                    ("item_key", payload.ItemKey),
                    ("previous_generation", payload.PreviousGeneration),
                    ("current_generation", payload.CurrentGeneration),
                    ("pr_number", payload.PrNumber),
                    ("merge_commit", payload.MergeCommit)),
            },
        ];
    }
}
