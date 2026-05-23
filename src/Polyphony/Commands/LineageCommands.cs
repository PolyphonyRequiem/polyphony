using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Manifest;
using Polyphony.Models;

namespace Polyphony.Commands;

/// <summary>
/// W15 (AB#3285): <c>polyphony lineage status --root &lt;id&gt;</c>
/// — pure read-only diagnostic that triangulates current lineage,
/// manifest lineage, and journal-observed lineages so an operator
/// (or the future <c>polyphony reconcile</c> verb) can quickly tell:
/// <list type="bullet">
///   <item>am I on the lineage the manifest expects?</item>
///   <item>does the journal know about THIS lineage at all?</item>
///   <item>is the journal partitioned (other lineages present but
///   not mine — fresh checkout, wiped tmpdir, cross-machine)?</item>
/// </list>
///
/// <para>Carries no side-effects — no writes to the journal, no
/// branch/PR fetches. Designed to be safe to invoke against any
/// in-flight run from any machine.</para>
///
/// <para>The forthcoming W11 schema additions
/// (<c>journal_lineages</c> table) will let this verb report retired
/// lineages too; until then "lineages" means "distinct run ids
/// observed in the existing <c>actions</c> table".</para>
/// </summary>
[VerbGroup("lineage")]
public sealed partial class LineageCommands(
    RunContext runContext,
    IJournalStore journalStore)
{
    /// <summary>
    /// Emit a <see cref="LineageStatusResult"/> envelope for
    /// <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Root work-item ID to scope the report to.</param>
    /// <param name="manifestPath">
    /// Override of the manifest path. Defaults to
    /// <c>.polyphony/run.yaml</c> resolved under
    /// <see cref="PolyphonyStatePaths"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    [VerbResult(typeof(LineageStatusResult))]
    public async Task<int> Status(
        int root = RequiredInput.MissingInt,
        string manifestPath = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lineage status",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var resolvedManifestPath = string.IsNullOrEmpty(manifestPath)
            ? RunManifestStore.DefaultRelativePath
            : manifestPath;

        string? manifestRunId = null;
        string? manifestError = null;
        if (!string.IsNullOrEmpty(resolvedManifestPath) && File.Exists(resolvedManifestPath))
        {
            try
            {
                var yaml = File.ReadAllText(resolvedManifestPath);
                var manifest = RunManifestStore.Parse(yaml, resolvedManifestPath);
                manifestRunId = string.IsNullOrEmpty(manifest.RunId) ? null : manifest.RunId;
            }
            catch (Exception ex)
            {
                manifestError = ex.Message;
            }
        }
        else
        {
            // Distinguish "no manifest at all" from "manifest exists
            // but unreadable" via ManifestError staying null here.
            resolvedManifestPath = null;
        }

        var lineages = await ReadLineagesAsync(root, ct).ConfigureAwait(false);
        var currentRunId = runContext.RunId;
        var currentHasRows = lineages.Any(l => string.Equals(l.RunId, currentRunId, StringComparison.Ordinal));
        var otherHasRows = lineages.Any(l => !string.Equals(l.RunId, currentRunId, StringComparison.Ordinal));
        var partitioned = !currentHasRows && otherHasRows;

        var result = new LineageStatusResult
        {
            Root = root,
            CurrentRunId = currentRunId,
            CurrentLineageIsManual = runContext.HasManualLineage,
            ManifestPath = resolvedManifestPath,
            ManifestRunId = manifestRunId,
            ManifestError = manifestError,
            JournalPath = journalStore is NullJournalStore ? null : journalStore.DatabasePath,
            Lineages = lineages,
            CurrentLineageHasRows = currentHasRows,
            LooksPartitioned = partitioned,
            Verdict = ComposeVerdict(
                currentRunId, runContext.HasManualLineage, manifestRunId,
                currentHasRows, partitioned, lineages.Count),
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.LineageStatusResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Walk the journal for every distinct run id under <paramref name="rootId"/>,
    /// counting rows and bounding first/last activity timestamps so a
    /// triage operator can spot "yesterday's stuck run" without
    /// running raw SQL. Also merges in W11's <c>journal_lineages</c>
    /// rows so retired lineages and lineages-with-no-actions surface
    /// alongside lineages-with-actions.
    /// </summary>
    private async Task<IReadOnlyList<LineageObservation>> ReadLineagesAsync(int rootId, CancellationToken ct)
    {
        if (journalStore is NullJournalStore) return Array.Empty<LineageObservation>();

        IReadOnlyList<JournalEntry> rows;
        try
        {
            rows = await journalStore.QueryAsync(new JournalQuery { RootId = rootId }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail open: diagnostic must not throw on a transient
            // journal error. Empty list means "we couldn't ask" — the
            // verdict logic still produces a useful result from the
            // remaining inputs.
            return Array.Empty<LineageObservation>();
        }

        IReadOnlyList<JournalLineage> tombstones;
        try
        {
            tombstones = await journalStore.GetLineagesAsync(rootId, ct).ConfigureAwait(false);
        }
        catch
        {
            tombstones = Array.Empty<JournalLineage>();
        }

        var byRunId = new Dictionary<string, LineageObservation>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(r => r.RunId, StringComparer.Ordinal))
        {
            byRunId[group.Key] = new LineageObservation
            {
                RunId = group.Key,
                RowCount = group.Count(),
                FirstSeenAt = group.Min(r => r.StartedAt),
                LastSeenAt = group.Max(r => r.FinishedAt ?? r.StartedAt),
                IsCurrent = string.Equals(group.Key, runContext.RunId, StringComparison.Ordinal),
            };
        }
        // Merge in W11 lineage rows. A lineage may exist in
        // journal_lineages without any actions (rare — e.g. attach
        // recorded a stub, or the lineage was retired before any
        // mutating action). Such lineages must still appear in the
        // status report so operators can see them.
        foreach (var tombstone in tombstones)
        {
            if (!byRunId.TryGetValue(tombstone.RunId, out var existing))
            {
                existing = new LineageObservation
                {
                    RunId = tombstone.RunId,
                    RowCount = 0,
                    FirstSeenAt = tombstone.CreatedAt,
                    LastSeenAt = tombstone.RetiredAt ?? tombstone.CreatedAt,
                    IsCurrent = string.Equals(tombstone.RunId, runContext.RunId, StringComparison.Ordinal),
                };
            }
            byRunId[tombstone.RunId] = existing with
            {
                RetiredAt = tombstone.RetiredAt,
                RetiredReason = tombstone.RetiredReason,
            };
        }

        return byRunId.Values
            .OrderBy(o => o.RunId, StringComparer.Ordinal)
            .ToList();
    }

    private string? TryResolveManifestPath(int rootId)
    {
        try
        {
            return RunManifestStore.DefaultRelativePath;
        }
        catch
        {
            return null;
        }
    }

    private static string ComposeVerdict(
        string currentRunId,
        bool isManual,
        string? manifestRunId,
        bool currentHasRows,
        bool partitioned,
        int distinctLineageCount)
    {
        if (isManual)
        {
            return "current_lineage_manual: no POLYPHONY_RUN_ID exported; verb running outside a launcher invocation.";
        }
        if (partitioned)
        {
            return $"journal_partitioned: {distinctLineageCount} other lineage(s) recorded for this root, but no rows for current run id '{currentRunId}'.";
        }
        if (manifestRunId is not null
            && !string.Equals(manifestRunId, currentRunId, StringComparison.Ordinal))
        {
            return $"manifest_lineage_mismatch: manifest carries run_id='{manifestRunId}' but current run id is '{currentRunId}'.";
        }
        if (currentHasRows)
        {
            return $"current_lineage_active: journal recognises run id '{currentRunId}' for this root.";
        }
        if (distinctLineageCount == 0)
        {
            return "no_journal_history: no journal rows for this root under any lineage.";
        }
        return "current_lineage_silent: journal has no rows for current lineage and the manifest agrees.";
    }
}
