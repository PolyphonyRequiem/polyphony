using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Models;
using Polyphony.Routing;
using Polyphony.Tagging;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony branch mark-impl-merged</c> /
/// <c>polyphony branch clear-impl-merged</c>: stamp or clear the
/// <c>polyphony:impl-merged-in-mg=&lt;mg-key&gt;</c> marker on a root
/// root work item (AB#3217 follow-up to AB#3169).
///
/// Authoritative tag-namespace owner is
/// <see cref="PolyphonyTags.ImplMergedInMgPrefix"/>; rationale for the
/// scheme lives in the doc-comment on that constant.
///
/// Both verbs follow the routing-style envelope contract: always exit 0;
/// surface success/failure in the JSON payload via
/// <see cref="BranchImplMergedMarkerResult.Success"/> and
/// <see cref="BranchImplMergedMarkerResult.Error"/>. They mirror the
/// AB#3189/3191 read-after-write defense from <c>branch next-impl</c> —
/// after the write + <c>twig sync</c>, re-fetch the item and assert the
/// tag state matches the requested terminal state. A silent regression
/// (twig push succeeds but cache reverts) would re-introduce the same
/// loop the marker exists to break, so the assertion fails loudly with
/// the work item URL.
/// </summary>
public sealed partial class BranchCommands
{
    /// <summary>
    /// Stamp the <c>polyphony:impl-merged-in-mg=&lt;mg-path&gt;</c> tag
    /// on a work item. Idempotent — if the tag is already present, the
    /// verb returns <c>Success=true, AlreadyInDesiredState=true</c>
    /// without writing.
    /// </summary>
    /// <param name="workItem">ADO work item ID to stamp the marker on (typically the root).</param>
    /// <param name="mgPath">Merge-group path the marker applies to — e.g. <c>pg-1</c> or nested <c>pg-1/pg-2</c>. Required; normalized via <see cref="PolyphonyTags.NormalizeMergeGroupKey(string)"/> before embedding in the tag.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("mark-impl-merged")]
    [JournaledAction(Action = "branch_mark_impl_merged")]
    [MutatesResource(ResourceKind.AdoWorkItemTag)]
    [VerbResult(typeof(BranchImplMergedMarkerResult))]
    public Task<int> MarkImplMerged(
        int workItem = RequiredInput.MissingInt,
        string mgPath = "",
        CancellationToken ct = default)
        => ApplyImplMergedMarkerAsync(workItem, mgPath, addTag: true, journalAction: "branch_mark_impl_merged", ct);

    /// <summary>
    /// Clear the <c>polyphony:impl-merged-in-mg=&lt;mg-path&gt;</c> tag
    /// from a work item. Idempotent — if the tag is already absent, the
    /// verb returns <c>Success=true, AlreadyInDesiredState=true</c>
    /// without writing. Used by every workflow route that re-dispatches
    /// the same MG for revision (scope_revise_counter,
    /// scope_revise_reset, user_acceptance Request Changes) so the
    /// marker doesn't suppress legitimate revision passes.
    /// </summary>
    /// <param name="workItem">ADO work item ID to clear the marker from.</param>
    /// <param name="mgPath">Merge-group path that identifies which marker to remove. Required; normalized via <see cref="PolyphonyTags.NormalizeMergeGroupKey(string)"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("clear-impl-merged")]
    [JournaledAction(Action = "branch_clear_impl_merged")]
    [MutatesResource(ResourceKind.AdoWorkItemTag)]
    [VerbResult(typeof(BranchImplMergedMarkerResult))]
    public Task<int> ClearImplMerged(
        int workItem = RequiredInput.MissingInt,
        string mgPath = "",
        CancellationToken ct = default)
        => ApplyImplMergedMarkerAsync(workItem, mgPath, addTag: false, journalAction: "branch_clear_impl_merged", ct);

    private async Task<int> ApplyImplMergedMarkerAsync(
        int workItem, string mgPath, bool addTag, string journalAction, CancellationToken ct)
    {
        var operation = addTag ? "mark" : "clear";
        if (RequiredInput.HaltIfMissing(
                $"branch {operation}-impl-merged",
                ("--work-item", workItem == RequiredInput.MissingInt),
                ("--mg-path", string.IsNullOrWhiteSpace(mgPath)))
            is { } halt)
            return halt;

        var normalizedKey = PolyphonyTags.NormalizeMergeGroupKey(mgPath);
        var tag = PolyphonyTags.ImplMergedInMg(mgPath);
        string? payloadJson = null;
        bool payloadSucceeded = false;
        bool payloadWasMutated = false;

        void CapturePayload(bool succeeded, bool wasMutated, bool alreadyInDesiredState, string? error)
        {
            payloadSucceeded = succeeded;
            payloadWasMutated = wasMutated;
            payloadJson = addTag
                ? JsonSerializer.Serialize(
                    new BranchMarkImplMergedPayload
                    {
                        WorkItemId = workItem,
                        MergeGroupPath = normalizedKey,
                        Tag = tag,
                        Operation = operation,
                        Succeeded = succeeded,
                        WasMutated = wasMutated,
                        AlreadyInDesiredState = alreadyInDesiredState,
                        Error = error,
                    },
                    PolyphonyJsonContext.Default.BranchMarkImplMergedPayload)
                : JsonSerializer.Serialize(
                    new BranchClearImplMergedPayload
                    {
                        WorkItemId = workItem,
                        MergeGroupPath = normalizedKey,
                        Tag = tag,
                        Operation = operation,
                        Succeeded = succeeded,
                        WasMutated = wasMutated,
                        AlreadyInDesiredState = alreadyInDesiredState,
                        Error = error,
                    },
                    PolyphonyJsonContext.Default.BranchClearImplMergedPayload);
        }

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation(journalAction, WorkItemJournalTarget(workItem), workItem, workItem),
            async innerCt =>
            {
                BranchImplMergedMarkerResult result;
                try
                {
                    // Sync first so we mutate a fresh local cache, not a snapshot
                    // that may already be stale by the time we re-read for the
                    // read-after-write assertion below.
                    await twig.SyncAsync(innerCt).ConfigureAwait(false);

                    var currentTags = await ReadTagsAsync(workItem, innerCt).ConfigureAwait(false);
                    var startsContained = currentTags.Contains(tag);

                    // No-op short-circuit: input already in the desired terminal
                    // state. Skip the patch + sync round-trip; return alongside
                    // AlreadyInDesiredState=true so callers can detect re-entry
                    // idempotency.
                    if (addTag == startsContained)
                    {
                        result = new BranchImplMergedMarkerResult
                        {
                            Operation = operation,
                            WorkItemId = workItem,
                            MergeGroupKey = normalizedKey,
                            Tag = tag,
                            Success = true,
                            AlreadyInDesiredState = true,
                        };
                        CapturePayload(succeeded: true, wasMutated: false, alreadyInDesiredState: true, error: null);
                        EmitImplMergedMarker(result);
                        return ExitCodes.Success;
                    }

                    var updatedTags = addTag ? currentTags.Add(tag) : currentTags.Remove(tag);

                    await twig.PatchFieldsAsync(workItem,
                        new Dictionary<string, string> { ["System.Tags"] = updatedTags.Format() },
                        innerCt).ConfigureAwait(false);

                    // Flush the staged tag patch to ADO. `twig patch` only mutates
                    // the local cache + pending queue; without this push the
                    // marker is invisible to the next `branch next-impl` call
                    // (the very call site this fix exists to influence). AB#3128
                    // pattern.
                    await twig.SyncAsync(innerCt).ConfigureAwait(false);

                    // AB#3189 / AB#3191 read-after-write defense — mirror of
                    // BranchCommands.NextImpl.cs lines 167-198. The push exited 0
                    // but the post-sync cache may still report the pre-patch
                    // tag set (ADO eventual-consistency race). Re-read and assert
                    // the tag is in the desired terminal state before declaring
                    // success — a silent failure here would re-introduce the
                    // exact loop this marker was designed to prevent.
                    var verifyTags = await ReadTagsAsync(workItem, innerCt).ConfigureAwait(false);
                    var endsContained = verifyTags.Contains(tag);
                    if (addTag != endsContained)
                    {
                        var workspace = await TryResolveAdoWorkspaceAsync(innerCt).ConfigureAwait(false);
                        var adoUrl = ComposeAdoWorkItemUrl(workspace, workItem);
                        var inspectSuffix = adoUrl.Length > 0 ? $" Inspect: {adoUrl}" : "";
                        var expectedDescription = addTag ? "present" : "absent";
                        var actualDescription = endsContained ? "present" : "absent";
                        var error =
                            $"Tag assertion failed for #{workItem} after branch {operation}-impl-merged: " +
                            $"expected tag '{tag}' to be {expectedDescription}, cache reports {actualDescription}. " +
                            $"twig patch + twig sync exited 0 but the change did not persist — " +
                            $"likely ADO eventual-consistency race or twig push regression." +
                            inspectSuffix;
                        result = new BranchImplMergedMarkerResult
                        {
                            Operation = operation,
                            WorkItemId = workItem,
                            MergeGroupKey = normalizedKey,
                            Tag = tag,
                            Success = false,
                            AlreadyInDesiredState = false,
                            Error = error,
                        };
                        CapturePayload(succeeded: false, wasMutated: false, alreadyInDesiredState: false, error: error);
                        EmitImplMergedMarker(result);
                        return ExitCodes.Success;
                    }

                    result = new BranchImplMergedMarkerResult
                    {
                        Operation = operation,
                        WorkItemId = workItem,
                        MergeGroupKey = normalizedKey,
                        Tag = tag,
                        Success = true,
                        AlreadyInDesiredState = false,
                    };
                    CapturePayload(succeeded: true, wasMutated: true, alreadyInDesiredState: false, error: null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var workspace = await TryResolveAdoWorkspaceAsync(innerCt).ConfigureAwait(false);
                    var adoUrl = ComposeAdoWorkItemUrl(workspace, workItem);
                    var inspectSuffix = adoUrl.Length > 0 ? $" Inspect: {adoUrl}" : "";
                    var error = $"Error applying impl-merged marker to #{workItem} ({operation}, mg-path='{mgPath}'): {ex.Message}.{inspectSuffix}";
                    result = new BranchImplMergedMarkerResult
                    {
                        Operation = operation,
                        WorkItemId = workItem,
                        MergeGroupKey = normalizedKey,
                        Tag = tag,
                        Success = false,
                        AlreadyInDesiredState = false,
                        Error = error,
                    };
                    CapturePayload(succeeded: false, wasMutated: false, alreadyInDesiredState: false, error: error);
                }

                EmitImplMergedMarker(result);
                return ExitCodes.Success;
            },
            outcomeSelector: exitCode => SelectJournalOutcome(exitCode, payloadSucceeded, payloadWasMutated),
            payloadSelector: _ => payloadJson,
            effectsSelector: _ => addTag
                ? SelectMarkImplMergedEffects(payloadJson is null
                    ? null
                    : JsonSerializer.Deserialize(payloadJson, PolyphonyJsonContext.Default.BranchMarkImplMergedPayload))
                : SelectClearImplMergedEffects(payloadJson is null
                    ? null
                    : JsonSerializer.Deserialize(payloadJson, PolyphonyJsonContext.Default.BranchClearImplMergedPayload)),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<TagSet> ReadTagsAsync(int workItemId, CancellationToken ct)
    {
        var item = await twig.ShowAsync(workItemId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Work item {workItemId} not found in twig cache after sync.");

        // twig's JSON shape exposes tags as either the convenience
        // `tags` property (when twig has normalized it) or the raw
        // `fields["System.Tags"]` ADO field. Match the SeedChildren
        // pattern at PlanCommands.SeedChildren.cs:925-926.
        var raw = item["tags"]?.GetValue<string>()
            ?? item["fields"]?["System.Tags"]?.GetValue<string>();
        return TagSet.Parse(raw);
    }

    private static void EmitImplMergedMarker(BranchImplMergedMarkerResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchImplMergedMarkerResult));
}
