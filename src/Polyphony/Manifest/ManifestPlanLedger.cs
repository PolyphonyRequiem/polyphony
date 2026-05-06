namespace Polyphony.Manifest;

/// <summary>
/// In-memory mutator for <see cref="RunManifest.PlanGenerations"/> +
/// <see cref="RunManifest.MergedPlanPrs"/>. Encapsulates the idempotency
/// semantics of "record this plan PR's merge" so both
/// <c>polyphony manifest record-plan-merge</c> and
/// <c>polyphony pr merge-plan-pr</c> apply identical rules without
/// drifting.
///
/// <para>The helper is pure with respect to disk: it mutates the
/// supplied <see cref="RunManifest"/> instance in place when it elects
/// to record, but never reads or writes the underlying YAML. Callers
/// own the load/save lifecycle.</para>
///
/// <para>Three outcomes per call:</para>
/// <list type="bullet">
///   <item><c>Recorded == true</c> — fresh ledger entry; <see cref="RunManifest.PlanGenerations"/>[itemKey] bumped by 1.</item>
///   <item><c>Recorded == false</c> AND <see cref="LedgerApplyResult.ConflictReason"/> is null — idempotent re-entry; the supplied <c>prNumber</c> already had a matching ledger entry. No mutation.</item>
///   <item><c>Recorded == false</c> AND <see cref="LedgerApplyResult.ConflictReason"/> is non-null — a conflicting prior entry exists for the same <c>prNumber</c> (different item key or different merge commit). No mutation. Caller must surface the reason.</item>
/// </list>
/// </summary>
public static class ManifestPlanLedger
{
    /// <summary>
    /// Apply a plan-PR merge record to the manifest in memory.
    /// </summary>
    /// <param name="manifest">Loaded manifest. Mutated when <see cref="LedgerApplyResult.Recorded"/> ends up true.</param>
    /// <param name="itemKey">Normalized plan key — <c>"root"</c> or numeric work-item id as a string.</param>
    /// <param name="prNumber">Positive PR number whose merge is being recorded.</param>
    /// <param name="mergeCommit">Non-empty merge commit SHA from the platform.</param>
    /// <param name="nowUtc">Timestamp written into a fresh ledger entry. Injected for tests.</param>
    public static LedgerApplyResult Apply(
        RunManifest manifest,
        string itemKey,
        int prNumber,
        string mergeCommit,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(itemKey);
        if (prNumber <= 0) throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number must be positive.");
        ArgumentException.ThrowIfNullOrEmpty(mergeCommit);

        var existing = manifest.MergedPlanPrs.FirstOrDefault(e => e.PrNumber == prNumber);
        if (existing is not null)
        {
            if (!string.Equals(existing.ItemKey, itemKey, StringComparison.Ordinal))
            {
                return new LedgerApplyResult
                {
                    Recorded = false,
                    PreviousGeneration = existing.PreviousGeneration,
                    CurrentGeneration = existing.CurrentGeneration,
                    ConflictReason =
                        $"PR #{prNumber} was previously recorded for item '{existing.ItemKey}'; cannot re-record for item '{itemKey}'.",
                };
            }

            if (!string.Equals(existing.MergeCommit, mergeCommit, StringComparison.Ordinal))
            {
                return new LedgerApplyResult
                {
                    Recorded = false,
                    PreviousGeneration = existing.PreviousGeneration,
                    CurrentGeneration = existing.CurrentGeneration,
                    ConflictReason =
                        $"PR #{prNumber} was previously recorded with merge commit '{existing.MergeCommit}'; cannot re-record with '{mergeCommit}'.",
                };
            }

            // Idempotent re-entry: matching ledger entry. No mutation.
            return new LedgerApplyResult
            {
                Recorded = false,
                PreviousGeneration = existing.PreviousGeneration,
                CurrentGeneration = existing.CurrentGeneration,
                ConflictReason = null,
            };
        }

        var previous = manifest.PlanGenerations.TryGetValue(itemKey, out var gen) ? gen : 0;
        var current = previous + 1;
        manifest.PlanGenerations[itemKey] = current;
        manifest.MergedPlanPrs.Add(new MergedPlanPrEntry
        {
            PrNumber = prNumber,
            ItemKey = itemKey,
            MergeCommit = mergeCommit,
            PreviousGeneration = previous,
            CurrentGeneration = current,
            RecordedAt = nowUtc,
        });

        return new LedgerApplyResult
        {
            Recorded = true,
            PreviousGeneration = previous,
            CurrentGeneration = current,
            ConflictReason = null,
        };
    }
}

/// <summary>Outcome of <see cref="ManifestPlanLedger.Apply"/>.</summary>
public sealed record LedgerApplyResult
{
    /// <summary>True iff the call mutated the manifest with a fresh ledger entry.</summary>
    public required bool Recorded { get; init; }

    /// <summary>Generation before the bump, or the prior recording's previous value on idempotent / conflicting outcomes.</summary>
    public required int PreviousGeneration { get; init; }

    /// <summary>Generation after the bump, or the prior recording's current value on idempotent / conflicting outcomes.</summary>
    public required int CurrentGeneration { get; init; }

    /// <summary>
    /// Non-null when an existing ledger entry for the same <c>prNumber</c>
    /// recorded a different item key or merge commit. Caller surfaces this
    /// as a config error; the manifest is unchanged.
    /// </summary>
    public string? ConflictReason { get; init; }
}
