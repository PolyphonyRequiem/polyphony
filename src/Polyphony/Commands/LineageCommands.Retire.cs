using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Models;

namespace Polyphony.Commands;

public sealed partial class LineageCommands
{
    /// <summary>
    /// W12 (AB#3293): tombstone a (run_id, root_id) lineage so future
    /// runs treat it as foreign. The reset pipeline calls this with
    /// <c>--all-active</c> after cleanup completes; operators can also
    /// invoke it directly with <c>--run-id</c> to retire a single
    /// surviving lineage from a botched run.
    ///
    /// <para>Retirement is a journal-bookkeeping concern, not a
    /// platform mutation: this verb writes only to the local
    /// <c>journal_lineages</c> table. Already-retired lineages are
    /// skipped silently (the verb is idempotent).</para>
    /// </summary>
    /// <param name="root">Root work-item ID to scope to.</param>
    /// <param name="runId">
    /// Specific run id to retire. Mutually exclusive with
    /// <paramref name="allActive"/>.
    /// </param>
    /// <param name="allActive">
    /// When true, retire every active (non-tombstoned) lineage for
    /// the root. Mutually exclusive with <paramref name="runId"/>.
    /// </param>
    /// <param name="reason">Free-form retirement reason (recorded).</param>
    /// <param name="execute">
    /// When false (default), report what would be retired without
    /// mutating the journal.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("retire")]
    [VerbResult(typeof(LineageRetireResult))]
    public async Task<int> Retire(
        int root = RequiredInput.MissingInt,
        string runId = "",
        bool allActive = false,
        string reason = "",
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lineage retire",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var hasRunId = !string.IsNullOrWhiteSpace(runId);
        if (hasRunId && allActive)
        {
            EmitRetire(new LineageRetireResult
            {
                Root = root,
                RunId = runId,
                AllActive = true,
                Executed = false,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = false,
                Error = "--run-id and --all-active are mutually exclusive.",
            });
            return ExitCodes.ConfigError;
        }
        if (!hasRunId && !allActive)
        {
            EmitRetire(new LineageRetireResult
            {
                Root = root,
                RunId = null,
                AllActive = false,
                Executed = false,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = false,
                Error = "Must specify exactly one of --run-id or --all-active.",
            });
            return ExitCodes.ConfigError;
        }

        var normalisedReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

        // NullJournalStore short-circuit: nothing to retire, but
        // succeed so the reset pipeline can tolerate a journal-less
        // env (e.g. unit tests that mock the journal layer).
        if (journalStore is NullJournalStore)
        {
            EmitRetire(new LineageRetireResult
            {
                Root = root,
                RunId = hasRunId ? runId : null,
                AllActive = allActive,
                Executed = execute,
                Reason = normalisedReason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = true,
            });
            return ExitCodes.Success;
        }

        IReadOnlyList<JournalLineage> lineages;
        try
        {
            lineages = await journalStore.GetLineagesAsync(root, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitRetire(new LineageRetireResult
            {
                Root = root,
                RunId = hasRunId ? runId : null,
                AllActive = allActive,
                Executed = execute,
                Reason = normalisedReason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = false,
                Error = $"Failed to enumerate lineages: {ex.Message}",
            });
            return ExitCodes.RoutingFailure;
        }

        var targets = hasRunId
            ? lineages.Where(l => string.Equals(l.RunId, runId, StringComparison.Ordinal)).ToList()
            : lineages.Where(l => l.RetiredAt is null).ToList();
        var skipped = lineages
            .Where(l => !targets.Contains(l) || l.RetiredAt is not null)
            .Select(l => l.RunId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // When run-id was specified but no matching row exists,
        // surface that as a skip + Success=true (idempotent: maybe
        // the lineage was already retired in a prior invocation).
        if (hasRunId && targets.Count == 0)
        {
            EmitRetire(new LineageRetireResult
            {
                Root = root,
                RunId = runId,
                AllActive = false,
                Executed = execute,
                Reason = normalisedReason,
                RetiredRunIds = [],
                SkippedRunIds = lineages.Select(l => l.RunId).Distinct(StringComparer.Ordinal).ToList(),
                Success = true,
            });
            return ExitCodes.Success;
        }

        var retired = new List<string>();
        var skippedRetiredIds = new List<string>();
        if (execute)
        {
            foreach (var lineage in targets)
            {
                if (lineage.RetiredAt is not null)
                {
                    skippedRetiredIds.Add(lineage.RunId);
                    continue;
                }
                try
                {
                    var didRetire = await journalStore.RetireLineageAsync(
                        lineage.RunId, root, normalisedReason, ct).ConfigureAwait(false);
                    if (didRetire)
                        retired.Add(lineage.RunId);
                    else
                        skippedRetiredIds.Add(lineage.RunId);
                }
                catch (Exception ex)
                {
                    EmitRetire(new LineageRetireResult
                    {
                        Root = root,
                        RunId = hasRunId ? runId : null,
                        AllActive = allActive,
                        Executed = true,
                        Reason = normalisedReason,
                        RetiredRunIds = retired,
                        SkippedRunIds = skippedRetiredIds,
                        Success = false,
                        Error = $"Failed to retire run id '{lineage.RunId}': {ex.Message}",
                    });
                    return ExitCodes.RoutingFailure;
                }
            }
        }
        else
        {
            // Dry-run: report what we WOULD retire.
            foreach (var lineage in targets)
            {
                if (lineage.RetiredAt is null)
                    retired.Add(lineage.RunId);
                else
                    skippedRetiredIds.Add(lineage.RunId);
            }
        }

        var combinedSkipped = skipped
            .Concat(skippedRetiredIds)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !retired.Contains(id, StringComparer.Ordinal))
            .ToList();

        EmitRetire(new LineageRetireResult
        {
            Root = root,
            RunId = hasRunId ? runId : null,
            AllActive = allActive,
            Executed = execute,
            Reason = normalisedReason,
            RetiredRunIds = retired,
            SkippedRunIds = combinedSkipped,
            Success = true,
        });
        return ExitCodes.Success;
    }

    private static void EmitRetire(LineageRetireResult result)
        => Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.LineageRetireResult));
}
