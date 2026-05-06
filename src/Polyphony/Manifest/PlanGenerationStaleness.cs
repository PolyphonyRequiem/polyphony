using System.Collections.Generic;
using System.Linq;

namespace Polyphony.Manifest;

/// <summary>
/// Compares an ancestor-plan-generation snapshot (embedded in a plan-PR
/// body's front-matter at the time the PR was opened) against the current
/// state of <see cref="RunManifest.PlanGenerations"/> on the manifest
/// branch.
///
/// <para>A snapshot entry is "stale" when an ancestor's generation has
/// advanced past the value the snapshot captured — i.e. someone merged a
/// later plan-PR for that ancestor while this plan-PR was open. Merging
/// a stale plan-PR would either silently rebase against an ancestor the
/// reviewer did not see (correctness hazard) or produce an inconsistent
/// promotion gate (operations hazard). The verb refuses both.</para>
///
/// <para>Three outcomes:</para>
/// <list type="bullet">
///   <item><b>Fresh</b> — every snapshot key matches the current manifest
///   value.</item>
///   <item><b>Stale</b> — at least one ancestor's current generation is
///   greater than its snapshot. Returned with the diff.</item>
///   <item><b>Unknown</b> — snapshot is empty (no front-matter, or
///   root-plan PR which has no ancestors). Caller decides whether to
///   accept (root) or refuse (descendant).</item>
/// </list>
///
/// <para>Snapshot keys missing from the manifest are <i>not</i> stale —
/// a key that the manifest never recorded means generation 0; if the
/// snapshot also says 0, that's a match; if the snapshot says > 0, that
/// is itself a corruption signal but we surface it as stale (the manifest
/// doesn't agree with the snapshot's claim).</para>
/// </summary>
public static class PlanGenerationStaleness
{
    public sealed record StalenessResult(
        bool IsStale,
        bool IsEmpty,
        IReadOnlyList<StaleEntry> StaleEntries);

    public sealed record StaleEntry(
        string AncestorKey,
        int SnapshotGeneration,
        int CurrentGeneration);

    /// <summary>Compares <paramref name="snapshot"/> against <paramref name="currentGenerations"/>.</summary>
    public static StalenessResult Check(
        IReadOnlyDictionary<string, int> snapshot,
        IReadOnlyDictionary<string, int> currentGenerations)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentGenerations);

        if (snapshot.Count == 0)
            return new StalenessResult(IsStale: false, IsEmpty: true, StaleEntries: []);

        var stale = new List<StaleEntry>();
        foreach (var (key, snapshotGen) in snapshot)
        {
            var currentGen = currentGenerations.TryGetValue(key, out var v) ? v : 0;
            if (currentGen > snapshotGen)
                stale.Add(new StaleEntry(key, snapshotGen, currentGen));
            else if (snapshotGen > currentGen)
                // Snapshot claims an advance the manifest never recorded.
                // Surface as stale so the operator investigates rather than
                // letting an inconsistent merge through.
                stale.Add(new StaleEntry(key, snapshotGen, currentGen));
        }

        return new StalenessResult(
            IsStale: stale.Count > 0,
            IsEmpty: false,
            StaleEntries: stale);
    }

    /// <summary>Renders a human-readable description of stale entries for error messages.</summary>
    public static string FormatStaleEntries(IReadOnlyList<StaleEntry> entries)
    {
        if (entries.Count == 0) return "(none)";
        return string.Join(", ", entries.Select(e =>
            $"{e.AncestorKey}: snapshot={e.SnapshotGeneration}, current={e.CurrentGeneration}"));
    }
}
