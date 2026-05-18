using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Tagging;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset state --apex N [--execute]</c> — stamps the
/// per-apex run-watermark tag (<c>polyphony:run-started-at=&lt;ISO-8601&gt;</c>)
/// on the apex root work item.
///
/// <para>This is the ONE writer of the watermark. The read side
/// (<see cref="Sdlc.Observers.PlanObserver"/>,
/// <see cref="StateCommands"/>) consults the watermark to distinguish
/// "satisfied by the current run" from "satisfied by any prior run"
/// — see <c>docs/decisions/run-reset.md</c>.</para>
///
/// <para><b>Semantics</b>:
/// <list type="bullet">
///   <item>Removes <b>every</b> existing <c>polyphony:run-started-at=*</c>
///         tag from the apex root before adding a fresh one (defense
///         against duplicate tags from a prior reset bug or operator
///         hand-edit). Duplicate count is reported via
///         <c>RemovedDuplicateTags</c>.</item>
///   <item>Always stamps the current UTC time formatted via
///         <see cref="PolyphonyTags.RunStartedAt(DateTimeOffset)"/>
///         (millisecond precision).</item>
///   <item>Not idempotent in the strict "no change" sense — every call
///         advances the watermark. That's intentional: the verb's whole
///         purpose is to bump it.</item>
/// </list></para>
///
/// <para><b>Dry-run</b> is the default. Pass <c>--execute</c> to actually
/// write. The dry-run envelope reports the would-be tag value and the
/// previous watermark so operators can preview the change.</para>
///
/// <para>Read-after-write defense (mirror of
/// <see cref="BranchCommands.MarkImplMerged"/>): after the patch + sync,
/// the verb re-reads the tag set and asserts the freshly-written
/// watermark is present and parses back to a value strictly greater
/// than the previous watermark (if any). A silent ADO eventual-
/// consistency revert is surfaced loudly.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Stamp the run-watermark tag on the apex root.
    /// </summary>
    /// <param name="apex">Apex root work-item ID — the work item that carries the watermark.</param>
    /// <param name="execute">Pass to perform the write. Without this flag, the verb runs in dry-run mode and emits the would-be outcome without mutating ADO.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("state")]
    [VerbResult(typeof(ResetStateResult))]
    public async Task<int> ResetState(
        int apex = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset state",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        ResetStateResult result;
        try
        {
            await _twig.SyncAsync(ct).ConfigureAwait(false);

            var currentTags = await ReadApexTagsAsync(apex, ct).ConfigureAwait(false);
            var previousWatermark = PolyphonyTags.ReadRunStartedAt(currentTags);
            var previousWatermarkText = previousWatermark is { } pw
                ? FormatWatermark(pw)
                : null;

            // Strip every existing run-started-at=* tag — there should be
            // at most one, but if a prior reset bug or operator hand-edit
            // left duplicates we want to converge on a single canonical
            // entry.
            var prefixEq = PolyphonyTags.RunStartedAtPrefix + "=";
            var duplicates = currentTags
                .Where(t => t.StartsWith(prefixEq, StringComparison.Ordinal))
                .ToList();

            var newInstant = DateTimeOffset.UtcNow;
            var newTag = PolyphonyTags.RunStartedAt(newInstant);
            var newWatermarkText = FormatWatermark(newInstant);

            if (!execute)
            {
                result = new ResetStateResult
                {
                    Apex = apex,
                    Success = true,
                    DryRun = true,
                    PreviousWatermark = previousWatermarkText,
                    NewWatermark = newWatermarkText,
                    RemovedDuplicateTags = Math.Max(0, duplicates.Count - 1),
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var stripped = duplicates.Aggregate(currentTags, (acc, t) => acc.Remove(t));
            var updated = stripped.Add(newTag);

            await _twig.PatchFieldsAsync(apex,
                new Dictionary<string, string> { ["System.Tags"] = updated.Format() },
                ct).ConfigureAwait(false);
            await _twig.SyncAsync(ct).ConfigureAwait(false);

            // Read-after-write: assert the watermark made it into the
            // cache. AB#3189/3191 pattern from mark-impl-merged.
            var verifyTags = await ReadApexTagsAsync(apex, ct).ConfigureAwait(false);
            var verifyWatermark = PolyphonyTags.ReadRunStartedAt(verifyTags);

            if (verifyWatermark is null)
            {
                result = new ResetStateResult
                {
                    Apex = apex,
                    Success = false,
                    DryRun = false,
                    PreviousWatermark = previousWatermarkText,
                    NewWatermark = newWatermarkText,
                    RemovedDuplicateTags = duplicates.Count,
                    Error =
                        $"Watermark assertion failed for #{apex} after reset state: " +
                        $"twig patch + sync exited 0 but no polyphony:run-started-at tag is " +
                        $"present in the cache — likely ADO eventual-consistency race or " +
                        $"twig push regression.",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            // Stamp drift > 60s would indicate the cache is returning a
            // truly-stale value — almost certainly an eventual-consistency
            // race. Surface so operators can rerun reset state.
            var drift = (newInstant - verifyWatermark.Value).Duration();
            if (drift > TimeSpan.FromMinutes(1))
            {
                result = new ResetStateResult
                {
                    Apex = apex,
                    Success = false,
                    DryRun = false,
                    PreviousWatermark = previousWatermarkText,
                    NewWatermark = newWatermarkText,
                    RemovedDuplicateTags = duplicates.Count,
                    Error =
                        $"Watermark stamp drifted by {drift.TotalSeconds:F0}s for #{apex} " +
                        $"after reset state: expected ~{newWatermarkText}, " +
                        $"cache reports {FormatWatermark(verifyWatermark.Value)}. " +
                        $"Likely ADO eventual-consistency race; re-run reset state.",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            result = new ResetStateResult
            {
                Apex = apex,
                Success = true,
                DryRun = false,
                PreviousWatermark = previousWatermarkText,
                NewWatermark = FormatWatermark(verifyWatermark.Value),
                RemovedDuplicateTags = Math.Max(0, duplicates.Count - 1),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetStateResult
            {
                Apex = apex,
                Success = false,
                DryRun = !execute,
                Error = $"Error stamping watermark on #{apex}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Read the apex root's tag set via <c>twig show</c>. Mirrors
    /// <c>BranchCommands.ReadTagsAsync</c> (re-implemented here to keep
    /// this partial self-contained).
    /// </summary>
    private async Task<TagSet> ReadApexTagsAsync(int apex, CancellationToken ct)
    {
        var item = await _twig.ShowAsync(apex, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Apex work item {apex} not found in twig cache after sync.");

        var raw = item["tags"]?.GetValue<string>()
            ?? item["fields"]?["System.Tags"]?.GetValue<string>();
        return TagSet.Parse(raw);
    }

    /// <summary>
    /// Canonical operator-facing serialisation of a watermark instant —
    /// matches the tag format (ms precision, UTC, ISO-8601). Used in
    /// envelope output and diagnostic strings so the human-readable
    /// timestamp is consistent with what landed in the tag.
    /// </summary>
    private static string FormatWatermark(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);

    private static void Emit(ResetStateResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetStateResult));
}
