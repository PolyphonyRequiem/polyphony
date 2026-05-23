namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// W10 (AB#3291) wrapper around <see cref="BranchLineageGuard"/>
    /// for the five <c>ensure-*</c> verbs. Centralises the lineage
    /// check at the moment a verb would adopt an existing remote or
    /// local branch. Returns <c>null</c> on allow, a structured
    /// refusal reason on refuse.
    ///
    /// <para>Only fires when the verb actually observes an existing
    /// branch — first-time creation in a fresh checkout never trips
    /// the guard. The grounding posture is symmetric with
    /// <see cref="PrCommands.CheckGhMergeLineageAsync"/>: refuse on
    /// positive foreign evidence; allow on silence; fail open on
    /// transient journal errors.</para>
    /// </summary>
    internal async Task<string?> CheckBranchAdoptionLineageAsync(
        string journalAction,
        string branchName,
        CancellationToken ct)
    {
        var decision = await BranchLineageGuard.CheckAsync(
            journal: _journalStore,
            currentRunId: _runContext.RunId,
            isManualLineage: _runContext.HasManualLineage,
            journalAction: journalAction,
            journalTarget: branchName,
            ct).ConfigureAwait(false);

        return decision.Allowed ? null : decision.Reason;
    }
}
