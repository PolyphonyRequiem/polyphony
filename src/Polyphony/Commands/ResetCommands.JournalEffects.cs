using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class ResetCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectResetRootEffects(ResetRootPayload? payload)
    {
        // reset_root delegates per-resource deletion to the projection
        // executor, which writes its own journal entries via the
        // per-kind IResourceDeleter pipeline. The root verb's own
        // journal entry records the composite outcome (StepsCompleted /
        // StepsFailed) and intentionally emits no resource effects.
        _ = payload;
        return [];
    }
}
