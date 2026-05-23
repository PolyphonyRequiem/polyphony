using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Models;

namespace Polyphony.Commands;

public sealed partial class LineageCommands
{
    /// <summary>
    /// W14 (AB#3295): record a local <c>journal_lineages</c> stub for a
    /// pre-existing run id so a fresh checkout can attach to an
    /// in-flight run instead of minting a parallel lineage.
    ///
    /// <para>The verb is the cross-machine on-ramp called out in B5 §2
    /// of the lineage design doc: on a second checkout (or wiped
    /// tmpdir), the journal is empty and would otherwise let a routine
    /// auto-fallback start a brand-new <c>manual_*</c> lineage. With
    /// <c>attach</c>, the operator pins a real ULID up front, so
    /// downstream verbs that consult <see cref="JournalLineage"/> (W11)
    /// see the lineage as already-current.</para>
    ///
    /// <para>This thin v1 only records the stub locally. A future
    /// enhancement will validate remote stamps before recording to
    /// catch typos. For now, callers are expected to supply a run id
    /// they've verified by other means (e.g. reading
    /// <c>.polyphony/run.yaml</c> from the feature branch).</para>
    /// </summary>
    /// <param name="root">Root work-item ID to scope to.</param>
    /// <param name="runId">ULID-shaped run id to attach.</param>
    /// <param name="reason">Free-form rationale (recorded as host comment).</param>
    /// <param name="execute">
    /// When false (default), report what would be attached without
    /// mutating the journal.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("attach")]
    [VerbResult(typeof(LineageAttachResult))]
    public async Task<int> Attach(
        int root = RequiredInput.MissingInt,
        string runId = "",
        string reason = "",
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lineage attach",
            ("--root", root == RequiredInput.MissingInt),
            ("--run-id", string.IsNullOrWhiteSpace(runId))) is { } halt)
            return halt;

        var normalisedReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

        if (root <= 0)
        {
            EmitAttach(new LineageAttachResult
            {
                Root = root, RunId = runId, Executed = false, Reason = normalisedReason,
                AlreadyExisted = false, Success = false,
                Error = "--root must be positive.",
            });
            return ExitCodes.ConfigError;
        }

        if (journalStore is NullJournalStore)
        {
            EmitAttach(new LineageAttachResult
            {
                Root = root, RunId = runId, Executed = execute, Reason = normalisedReason,
                AlreadyExisted = false, Success = true,
            });
            return ExitCodes.Success;
        }

        IReadOnlyList<JournalLineage> existing;
        try
        {
            existing = await journalStore.GetLineagesAsync(root, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitAttach(new LineageAttachResult
            {
                Root = root, RunId = runId, Executed = false, Reason = normalisedReason,
                AlreadyExisted = false, Success = false,
                Error = $"Failed to enumerate lineages: {ex.Message}",
            });
            return ExitCodes.RoutingFailure;
        }

        var match = existing.FirstOrDefault(l => string.Equals(l.RunId, runId, StringComparison.Ordinal));
        var alreadyExisted = match is not null;

        // If a non-retired row is already present, attach is a no-op
        // success. Retired rows are also treated as already-existing;
        // attach refuses rather than silently un-retiring (operators
        // should use a fresh run id rather than resurrect a tombstone).
        if (alreadyExisted && match!.RetiredAt is not null)
        {
            EmitAttach(new LineageAttachResult
            {
                Root = root, RunId = runId, Executed = false, Reason = normalisedReason,
                AlreadyExisted = true, Success = false,
                Error = $"Lineage '{runId}' for root {root} is retired; mint a new run id instead.",
            });
            return ExitCodes.RoutingFailure;
        }

        if (alreadyExisted)
        {
            EmitAttach(new LineageAttachResult
            {
                Root = root, RunId = runId, Executed = execute, Reason = normalisedReason,
                AlreadyExisted = true, Success = true,
            });
            return ExitCodes.Success;
        }

        if (execute)
        {
            try
            {
                await journalStore.RecordLineageAsync(
                    runId, root, Environment.MachineName, Environment.UserName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EmitAttach(new LineageAttachResult
                {
                    Root = root, RunId = runId, Executed = true, Reason = normalisedReason,
                    AlreadyExisted = false, Success = false,
                    Error = $"Failed to record lineage: {ex.Message}",
                });
                return ExitCodes.RoutingFailure;
            }
        }

        EmitAttach(new LineageAttachResult
        {
            Root = root, RunId = runId, Executed = execute, Reason = normalisedReason,
            AlreadyExisted = false, Success = true,
        });
        return ExitCodes.Success;
    }

    private static void EmitAttach(LineageAttachResult result)
        => Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.LineageAttachResult));
}
