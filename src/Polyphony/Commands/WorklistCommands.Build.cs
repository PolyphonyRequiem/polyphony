using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worklist build</c> — compute the ordered, wave-grouped
/// list of plan-tree work items that the (future) tree-walker workflow
/// will dispatch in parallel.
///
/// <para>Pure inspection verb: walks children from
/// <see cref="Twig.Domain.Interfaces.IWorkItemRepository"/> in BFS order,
/// reads <c>.polyphony/run.yaml</c> for plan-PR ledger entries and
/// generation counters, and emits a <see cref="WorklistResult"/>. No
/// manifest mutation, no platform calls.</para>
///
/// <para>Wave assignment is by plan-tree depth — wave 0 is the root
/// only, wave N contains the items whose parent sits in wave N-1.
/// Plan-tree depth is the BFS depth from the root, which is NOT
/// necessarily the same as the work-item-type depth (an Epic with a
/// Task child sits at wave 1, even though the type ladder normally
/// inserts Issue between them).</para>
/// </summary>
public sealed partial class WorklistCommands
{
    /// <summary>
    /// Build a worklist for the given run root.
    /// </summary>
    /// <param name="rootId">Run-root work-item id (positive). MUST match
    /// the manifest's <see cref="RunManifest.RootId"/>; mismatch is an
    /// error so the dispatcher cannot accidentally drive the wrong run.</param>
    /// <param name="manifestPath">Path to <c>.polyphony/run.yaml</c>.
    /// Defaults to <c>.polyphony/run.yaml</c> resolved relative to the
    /// current working directory.</param>
    /// <param name="json">Emit machine-readable JSON instead of the
    /// human-readable wave summary. The JSON shape is
    /// <see cref="WorklistResult"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("build")]
    public async Task<int> Build(
        int rootId,
        string manifestPath = ".polyphony/run.yaml",
        bool json = false,
        CancellationToken ct = default)
    {
        if (rootId <= 0)
        {
            EmitWorklist(new WorklistResult
            {
                RootId = rootId,
                Waves = Array.Empty<WorklistWave>(),
                Error = $"--root-id must be positive (got {rootId})",
                ErrorCode = "invalid_argument",
            }, json);
            return ExitCodes.Success;
        }

        // Load manifest. Routing-style: missing/malformed both surface as
        // categorical error codes the workflow can branch on.
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(manifestPath);
        }
        catch (FileNotFoundException ex)
        {
            EmitWorklist(new WorklistResult
            {
                RootId = rootId,
                Waves = Array.Empty<WorklistWave>(),
                Error = ex.Message,
                ErrorCode = "manifest_not_found",
            }, json);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitWorklist(new WorklistResult
            {
                RootId = rootId,
                Waves = Array.Empty<WorklistWave>(),
                Error = ex.Message,
                ErrorCode = "manifest_invalid",
            }, json);
            return ExitCodes.Success;
        }

        if (manifest.RootId != rootId)
        {
            EmitWorklist(new WorklistResult
            {
                RootId = rootId,
                Waves = Array.Empty<WorklistWave>(),
                Error = $"Manifest root_id {manifest.RootId} does not match --root-id {rootId}",
                ErrorCode = "root_id_mismatch",
            }, json);
            return ExitCodes.Success;
        }

        // BFS the plan tree from the root, building wave-by-wave. Wave 0
        // is always the root (even when twig has no record of it — we
        // surface that via plan_status=unknown so the dispatcher gets a
        // non-empty result it can act on).
        var waves = await BuildWavesAsync(rootId, manifest, ct).ConfigureAwait(false);

        EmitWorklist(new WorklistResult
        {
            RootId = rootId,
            Waves = waves,
        }, json);
        return ExitCodes.Success;
    }

    /// <summary>
    /// BFS the plan tree from <paramref name="rootId"/>, grouping items
    /// by depth into waves. Each item is annotated with its parent id,
    /// plan status, latest plan-PR number, and current generation.
    /// </summary>
    /// <remarks>
    /// We deduplicate ids defensively even though the twig cache treats
    /// children as a strict tree — the verb is read-only and a malformed
    /// cache should not produce duplicate items in the worklist.
    /// </remarks>
    private async Task<IReadOnlyList<WorklistWave>> BuildWavesAsync(
        int rootId,
        RunManifest manifest,
        CancellationToken ct)
    {
        var waves = new List<WorklistWave>();
        var seen = new HashSet<int> { rootId };

        // Wave 0: the root. parent=0 marks "no parent in the worklist".
        var rootItem = BuildItem(rootId, parentItemId: 0, manifest, rootId, knownInTwig: true);
        // We don't actually know yet whether the root is in twig — peek
        // and override if missing.
        var rootCacheEntry = await _repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (rootCacheEntry is null)
        {
            rootItem = BuildItem(rootId, parentItemId: 0, manifest, rootId, knownInTwig: false);
        }
        waves.Add(new WorklistWave(0, new[] { rootItem }));

        // Walk subsequent waves until none of the previous wave's items
        // have any (unseen) children.
        var previousWave = new List<int> { rootId };
        var waveIndex = 1;
        while (previousWave.Count > 0)
        {
            var nextWaveItems = new List<WorklistItem>();
            var nextWaveIds = new List<int>();

            foreach (var parentId in previousWave)
            {
                ct.ThrowIfCancellationRequested();
                var children = await _repository.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
                // Stable order: by id ascending. The twig repository does
                // not guarantee an order; we want determinism for the
                // dispatcher and for tests.
                foreach (var child in children.OrderBy(c => c.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    nextWaveItems.Add(BuildItem(child.Id, parentItemId: parentId, manifest, rootId, knownInTwig: true));
                    nextWaveIds.Add(child.Id);
                }
            }

            if (nextWaveItems.Count == 0) break;

            waves.Add(new WorklistWave(waveIndex, nextWaveItems));
            previousWave = nextWaveIds;
            waveIndex++;
        }

        return waves;
    }

    /// <summary>
    /// Build a <see cref="WorklistItem"/> by joining the in-tree id /
    /// parent with the manifest's plan-PR ledger and generation map.
    /// </summary>
    private static WorklistItem BuildItem(
        int itemId,
        int parentItemId,
        RunManifest manifest,
        int rootId,
        bool knownInTwig)
    {
        // Manifest keys are "root" for the run root, numeric strings for
        // descendants (matches the schema enforced by RunManifestValidator).
        var key = itemId == rootId
            ? "root"
            : itemId.ToString(CultureInfo.InvariantCulture);

        var generation = manifest.PlanGenerations.TryGetValue(key, out var gen) ? gen : 0;

        // Latest merged plan-PR for this item, by RecordedAt (PrNumber as
        // a tiebreaker). Mirrors the rule used by `polyphony plan status`.
        MergedPlanPrEntry? latest = null;
        foreach (var entry in manifest.MergedPlanPrs)
        {
            if (!string.Equals(entry.ItemKey, key, StringComparison.Ordinal)) continue;
            if (latest is null
                || entry.RecordedAt > latest.RecordedAt
                || (entry.RecordedAt == latest.RecordedAt && entry.PrNumber > latest.PrNumber))
            {
                latest = entry;
            }
        }

        string status;
        int? planPrNumber;
        if (!knownInTwig)
        {
            status = "unknown";
            planPrNumber = latest?.PrNumber;
        }
        else if (latest is not null)
        {
            status = "merged";
            planPrNumber = latest.PrNumber;
        }
        else
        {
            status = "pending";
            planPrNumber = null;
        }

        return new WorklistItem(
            ItemId: itemId,
            ParentItemId: parentItemId,
            PlanStatus: status,
            PlanPrNumber: planPrNumber,
            CurrentGeneration: generation);
    }

    private static void EmitWorklist(WorklistResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                result, PolyphonyJsonContext.Default.WorklistResult));
            return;
        }

        Console.WriteLine(RenderHuman(result));
    }

    /// <summary>
    /// Renders the human-readable form. Layout:
    /// <code>
    /// worklist: root=100  waves=3
    ///   wave 0:
    ///     item 100  parent=0    status=merged   pr=#42  generation=1
    ///   wave 1:
    ///     item 250  parent=100  status=pending  generation=0
    ///     item 310  parent=100  status=pending  generation=0
    /// </code>
    /// Errors render as a single line prefixed with <c>error:</c>.
    /// </summary>
    private static string RenderHuman(WorklistResult result)
    {
        var sb = new StringBuilder();
        sb.Append("worklist: root=").Append(result.RootId.ToString(CultureInfo.InvariantCulture));

        if (result.Error is not null)
        {
            sb.AppendLine();
            sb.Append("  error: ").Append(result.Error);
            if (!string.IsNullOrEmpty(result.ErrorCode))
            {
                sb.Append(" (").Append(result.ErrorCode).Append(')');
            }
            return sb.ToString();
        }

        sb.Append("  waves=").Append(result.Waves.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        foreach (var wave in result.Waves)
        {
            sb.Append("  wave ").Append(wave.WaveIndex.ToString(CultureInfo.InvariantCulture)).AppendLine(":");
            foreach (var item in wave.Items)
            {
                sb.Append("    item ").Append(item.ItemId.ToString(CultureInfo.InvariantCulture));
                sb.Append("  parent=").Append(item.ParentItemId.ToString(CultureInfo.InvariantCulture));
                sb.Append("  status=").Append(item.PlanStatus);
                if (item.PlanPrNumber is { } pr)
                {
                    sb.Append("  pr=#").Append(pr.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append("  generation=").Append(item.CurrentGeneration.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }
}
