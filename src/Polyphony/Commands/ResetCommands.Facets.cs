using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Tagging;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset facets --root N [--execute]</c> — strips the two
/// persisted "planning is already done" tags
/// (<c>polyphony:facets=&lt;csv&gt;</c> and <c>polyphony:planned</c>)
/// from the root root and every descendant in scope.
///
/// <para><b>Why this exists.</b> The watermark mechanism
/// (<see cref="ResetState"/>) can only filter merged-PR observations by
/// merge time. The facet override + planned marker are persisted
/// decisions, not PR observations — they survive watermark advancement
/// and will silently steer the next <c>state classify-lifecycle</c> call
/// to <c>implement-merge-group</c> for a work item whose plan branch
/// has just been thrown away. See <c>docs/decisions/run-reset.md</c>
/// §"Why facets cleanup is separate from watermark" for the root
/// 62286666 incident write-up.</para>
///
/// <para><b>Scope.</b> Walks the root subtree via <see cref="Routing.HierarchyWalker"/>
/// (the planner can stamp facets/planned tags on any plannable parent,
/// not just the root root). Items with no targeted tags are silently
/// skipped — only items that actually had a tag to remove appear in
/// <see cref="ResetFacetsResult.Items"/>.</para>
///
/// <para><b>Read-after-write defense.</b> Mirrors
/// <see cref="BranchCommands.MarkImplMerged"/>: after every per-item
/// patch + <c>twig sync</c>, the verb re-reads the tag set and asserts
/// the targeted tags are absent. A silent ADO eventual-consistency
/// revert surfaces loudly in
/// <see cref="ResetFacetsItem.Verified"/> = false.</para>
///
/// <para><b>Per-item failure tolerance.</b> Per the reset-family
/// contract, a single item that fails to patch does NOT halt the walk;
/// it surfaces as a <see cref="ResetFacetsItem.Verified"/> = false
/// entry with a non-null <see cref="ResetFacetsItem.Error"/>. The
/// verb's overall <see cref="ResetFacetsResult.Success"/> remains true
/// in that case (it reflects "the walk completed", not "every item
/// succeeded"). A verb-wide failure (sync threw, walk threw) flips
/// <see cref="ResetFacetsResult.Success"/> to false.</para>
///
/// <para><b>Dry-run.</b> Default. Pass <c>--execute</c> to actually
/// patch tags. Dry-run reports the would-be removals so operators can
/// preview the cleanup.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Strip the persisted planning-completion tags from the root
    /// subtree.
    /// </summary>
    /// <param name="root">Root root work-item ID — the head of the subtree to walk.</param>
    /// <param name="execute">Pass to actually patch tags. Without this flag, the verb runs in dry-run mode and emits the would-be removals without mutating ADO.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("facets")]
    [VerbResult(typeof(ResetFacetsResult))]
    [JournaledAction(Action = "reset_facets")]
    [MutatesResource(ResourceKind.AdoWorkItemTag)]
    public Task<int> ResetFacets(
        int root = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset facets",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return Task.FromResult(halt);

        return JournalCommandSupport.RunWithCapturedResultAsync<ResetFacetsResult, ResetFacetsPayload>(
            _journalDecorator,
            _runContext,
            "reset_facets",
            $"root:{root}",
            innerCt => ResetFacetsCoreAsync(root, execute, innerCt),
            PolyphonyJsonContext.Default.ResetFacetsResult,
            (_, result) => new ResetFacetsPayload
            {
                Root = result?.Root ?? root,
                DryRun = result?.DryRun ?? !execute,
                Succeeded = result?.Success ?? false,
                WasMutated = result is not null && !result.DryRun && result.Items.Any(item => item.Verified == true),
                ItemsScanned = result?.ItemsScanned ?? 0,
                ItemsModified = result?.ItemsModified ?? 0,
                TotalFacetTagsRemoved = result?.TotalFacetTagsRemoved ?? 0,
                TotalPlannedTagsRemoved = result?.TotalPlannedTagsRemoved ?? 0,
                Items = result?.Items ?? [],
                Error = result?.Error,
            },
            PolyphonyJsonContext.Default.ResetFacetsPayload,
            payload => payload.Succeeded,
            payload => payload.WasMutated,
            SelectResetFacetsEffects,
            ct,
            rootId: root);
    }

    private async Task<int> ResetFacetsCoreAsync(
        int root,
        bool execute,
        CancellationToken ct)
    {
        ResetFacetsResult result;
        try
        {
            // Sync first so the walk + per-item reads observe a fresh
            // local cache. Without this, tags freshly stamped by a
            // just-finished prior process may still be staged in twig's
            // pending queue and absent from `twig show`.
            await _twig.SyncAsync(ct).ConfigureAwait(false);

            // maxDepth 16 is generous for CMMI work-item trees (Epic →
            // Scenario → Deliverable → Task Group → Task is 5 levels;
            // 16 covers any reasonable nesting + future type additions).
            var hierarchy = await _walker.WalkAsync(root, maxDepth: 16, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                result = new ResetFacetsResult
                {
                    Root = root,
                    Success = false,
                    DryRun = !execute,
                    Error = $"Root work item {root} not found in twig cache after sync.",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var allItems = new List<HierarchyResult>();
            FlattenHierarchy(hierarchy, allItems);

            var items = new List<ResetFacetsItem>();
            var itemsModified = 0;
            var totalFacetTagsRemoved = 0;
            var totalPlannedTagsRemoved = 0;
            var facetsPrefixEq = PolyphonyTags.FacetsPrefix + "=";

            foreach (var item in allItems)
            {
                ct.ThrowIfCancellationRequested();

                var tags = TagSet.Parse(item.Tags);
                var facetTagsToRemove = tags
                    .Where(t => t.StartsWith(facetsPrefixEq, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var hasPlannedTag = tags.Contains(PolyphonyTags.Planned);

                if (facetTagsToRemove.Count == 0 && !hasPlannedTag)
                    continue; // nothing to do for this item

                if (!execute)
                {
                    items.Add(new ResetFacetsItem
                    {
                        WorkItemId = item.WorkItemId,
                        FacetTagsRemoved = facetTagsToRemove,
                        PlannedTagRemoved = hasPlannedTag,
                        Verified = null,
                    });
                    itemsModified++;
                    totalFacetTagsRemoved += facetTagsToRemove.Count;
                    if (hasPlannedTag) totalPlannedTagsRemoved++;
                    continue;
                }

                try
                {
                    var updated = facetTagsToRemove.Aggregate(tags, (acc, t) => acc.Remove(t));
                    if (hasPlannedTag) updated = updated.Remove(PolyphonyTags.Planned);

                    await _twig.PatchFieldsAsync(item.WorkItemId,
                        new Dictionary<string, string> { ["System.Tags"] = updated.Format() },
                        ct).ConfigureAwait(false);

                    // Flush the staged patch to ADO before re-reading.
                    // Same staged-but-never-pushed failure mode as
                    // BranchCommands.MarkImplMerged (AB#3128 pattern).
                    await _twig.SyncAsync(ct).ConfigureAwait(false);

                    // Read-after-write defense (AB#3189/3191 pattern,
                    // mirror of BranchCommands.MarkImplMerged): twig
                    // patch + sync exited 0 but the post-sync cache may
                    // still report the pre-patch tag set under ADO
                    // eventual-consistency. Re-read and assert the
                    // targeted tags are gone — a silent regression here
                    // would re-introduce the exact bug this verb
                    // exists to fix.
                    var verifyTags = await ReadApexTagsAsync(item.WorkItemId, ct).ConfigureAwait(false);
                    var stillHasFacets = verifyTags.Any(t =>
                        t.StartsWith(facetsPrefixEq, StringComparison.OrdinalIgnoreCase));
                    var stillHasPlanned = verifyTags.Contains(PolyphonyTags.Planned);

                    if (stillHasFacets || stillHasPlanned)
                    {
                        var detail = stillHasFacets && stillHasPlanned
                            ? "polyphony:facets=* AND polyphony:planned"
                            : stillHasFacets ? "polyphony:facets=*" : "polyphony:planned";
                        items.Add(new ResetFacetsItem
                        {
                            WorkItemId = item.WorkItemId,
                            FacetTagsRemoved = facetTagsToRemove,
                            PlannedTagRemoved = hasPlannedTag,
                            Verified = false,
                            Error =
                                $"Tag assertion failed for #{item.WorkItemId} after reset facets: " +
                                $"expected {detail} to be absent, cache reports them still present. " +
                                $"twig patch + twig sync exited 0 but the change did not persist — " +
                                $"likely ADO eventual-consistency race or twig push regression.",
                        });
                        continue;
                    }

                    items.Add(new ResetFacetsItem
                    {
                        WorkItemId = item.WorkItemId,
                        FacetTagsRemoved = facetTagsToRemove,
                        PlannedTagRemoved = hasPlannedTag,
                        Verified = true,
                    });
                    itemsModified++;
                    totalFacetTagsRemoved += facetTagsToRemove.Count;
                    if (hasPlannedTag) totalPlannedTagsRemoved++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    items.Add(new ResetFacetsItem
                    {
                        WorkItemId = item.WorkItemId,
                        FacetTagsRemoved = facetTagsToRemove,
                        PlannedTagRemoved = hasPlannedTag,
                        Verified = false,
                        Error = $"Error patching tags on #{item.WorkItemId}: {ex.Message}",
                    });
                }
            }

            result = new ResetFacetsResult
            {
                Root = root,
                Success = true,
                DryRun = !execute,
                ItemsScanned = allItems.Count,
                ItemsModified = itemsModified,
                TotalFacetTagsRemoved = totalFacetTagsRemoved,
                TotalPlannedTagsRemoved = totalPlannedTagsRemoved,
                Items = items,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetFacetsResult
            {
                Root = root,
                Success = false,
                DryRun = !execute,
                Error = $"Error walking hierarchy for root #{root}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Depth-first flatten of a <see cref="HierarchyResult"/> tree into a
    /// list. Order is parent-then-children so the root root is always
    /// at index 0 — useful for operators reading the dry-run output.
    /// </summary>
    private static void FlattenHierarchy(HierarchyResult node, List<HierarchyResult> sink)
    {
        sink.Add(node);
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
                FlattenHierarchy(child, sink);
        }
    }

    private static void Emit(ResetFacetsResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetFacetsResult));
}
