using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Manifest;
using Polyphony.Sdlc;
using Twig.Domain.Aggregates;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worklist build</c> — compute the ordered, wave-grouped
/// list of plan-tree work items that the apex driver workflow will
/// dispatch in parallel.
///
/// <para>Pure inspection verb: walks children from
/// <see cref="Twig.Domain.Interfaces.IWorkItemRepository"/> in BFS order,
/// derives each item's <see cref="RequirementSet"/> via the same
/// <see cref="RequirementInputResolver"/> + <see cref="RequirementSetDeriver"/>
/// + <see cref="ExecutionModeInjector"/> composition used by
/// <c>state next-ready</c> and <c>edges check</c>, builds a single
/// <see cref="EdgeGraph"/> over the resulting requirement sets, reads
/// the local run manifest under <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/run.yaml</c>
/// for plan-PR ledger entries and generation counters, and emits a
/// <see cref="WorklistResult"/>. No manifest mutation, no platform calls.</para>
///
/// <para>Wave assignment is by topological depth over the union graph
/// (within-item edges ∪ definitional cross-item edges, plus mode-injected
/// edges per <see cref="ExecutionModeInjector"/>). Wave 0 contains items
/// whose entry requirements have no inbound cross-item edges — typically
/// the run root, plus items whose parent is a non-plannable container.
/// On a definitional plan tree (every parent plannable + decomposable),
/// the topological order coincides with BFS depth from the root.</para>
///
/// <para>Routing-style verb: ALWAYS exits 0. Errors live in the
/// envelope's <see cref="WorklistResult.Error"/> /
/// <see cref="WorklistResult.ErrorCode"/> fields; conflicts live in
/// <see cref="WorklistResult.HasConflicts"/> /
/// <see cref="WorklistResult.Conflicts"/>. When conflicts are present
/// <see cref="WorklistResult.Waves"/> is the empty array — explicit
/// emptiness is the contract for downstream consumers.</para>
/// </summary>
public sealed partial class WorklistCommands
{
    /// <summary>
    /// Build a worklist for the given run root.
    /// </summary>
    /// <param name="rootId">Run-root work-item id (positive). MUST match
    /// the manifest's <see cref="RunManifest.RootId"/>; mismatch is an
    /// error so the dispatcher cannot accidentally drive the wrong run.</param>
    /// <param name="manifestPath">Optional override of the run manifest
    /// path. When empty (default), derived under the git common dir via
    /// <see cref="Polyphony.Infrastructure.Paths.PolyphonyStatePaths"/>.
    /// Pass an explicit path only as a testing seam.</param>
    /// <param name="json">Emit machine-readable JSON instead of the
    /// human-readable wave summary. The JSON shape is
    /// <see cref="WorklistResult"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("build")]
    [VerbResult(typeof(WorklistResult))]
    public async Task<int> Build(
        int rootId = RequiredInput.MissingInt,
        string manifestPath = "",
        bool json = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("worklist build",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            EmitWorklist(EmptyResult(rootId, $"--root-id must be positive (got {rootId})", "invalid_argument"), json);
            return ExitCodes.Success;
        }

        // Rev 4.2: resolve the local manifest path. When the caller passes
        // --manifest-path explicitly we honor it (testing seam); otherwise
        // PolyphonyStatePaths derives <git-common-dir>/polyphony/{rootId}/run.yaml.
        var resolvedPath = await ManifestPathHelper.ResolveAsync(_statePaths, rootId, manifestPath, ct).ConfigureAwait(false);
        if (resolvedPath.Error is not null)
        {
            EmitWorklist(EmptyResult(rootId, resolvedPath.Error, "manifest_path_resolution_failed"), json);
            return ExitCodes.Success;
        }
        var localManifestPath = resolvedPath.Path;

        // Load manifest. Routing-style: missing/malformed both surface as
        // categorical error codes the workflow can branch on.
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(localManifestPath);
        }
        catch (FileNotFoundException)
        {
            EmitWorklist(EmptyResult(rootId, $"manifest not found at {localManifestPath}", "manifest_not_found"), json);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitWorklist(EmptyResult(rootId, ex.Message, "manifest_invalid"), json);
            return ExitCodes.Success;
        }

        if (manifest.RootId != rootId)
        {
            EmitWorklist(EmptyResult(rootId, $"Manifest root_id {manifest.RootId} does not match --root-id {rootId}", "root_id_mismatch"), json);
            return ExitCodes.Success;
        }

        // Walk the subtree → flat list of (item, parentId). The walk is
        // tree-shape only; per-item requirement derivation happens next.
        List<WalkedItem> walked;
        try
        {
            walked = await WalkSubtreeAsync(rootId, ct).ConfigureAwait(false);
        }
        catch (CollectionFailureException ex)
        {
            EmitWorklist(EmptyResult(rootId, ex.Message, ex.Code), json);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitWorklist(EmptyResult(rootId, ex.Message, "cache_error"), json);
            return ExitCodes.Success;
        }

        // Resolve per-item inputs → derive RequirementSet → inject the
        // execution_mode edges. Mirrors the composition used by
        // state next-ready and edges check.
        List<EdgeGraphInput> inputs;
        try
        {
            inputs = await BuildEdgeGraphInputsAsync(walked, ct).ConfigureAwait(false);
        }
        catch (CollectionFailureException ex)
        {
            EmitWorklist(EmptyResult(rootId, ex.Message, ex.Code), json);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitWorklist(EmptyResult(rootId, ex.Message, "cache_error"), json);
            return ExitCodes.Success;
        }

        EdgeGraph graph;
        try
        {
            graph = EdgeGraph.Build(inputs);
        }
        catch (Exception ex)
        {
            // Safety net — Build only throws on empty or duplicate inputs;
            // both are programmer errors here, but routing keeps a stable
            // envelope shape rather than a stack trace.
            EmitWorklist(EmptyResult(rootId, ex.Message, "graph_invalid"), json);
            return ExitCodes.Success;
        }

        // Project EdgeGraph conflicts through the same envelope shape that
        // `edges check` uses, so workflow consumers can share rendering
        // code regardless of which verb produced the report.
        var conflicts = graph.Conflicts.Select(c => new EdgesCheckConflict
        {
            Kind = c.Kind,
            Description = c.Description,
            ContributingEdges = c.ContributingEdges,
        }).ToArray();

        if (conflicts.Length > 0)
        {
            // Conflict gate: emit empty waves + populated conflicts. Exit 0
            // routing-style — the apex driver decides whether to halt.
            EmitWorklist(new WorklistResult
            {
                RootId = rootId,
                ItemsWalked = walked.Count,
                HasConflicts = true,
                Conflicts = conflicts,
                Waves = Array.Empty<WorklistWave>(),
            }, json);
            return ExitCodes.Success;
        }

        var waves = ProjectWaves(graph.ToWaves(), walked, manifest, rootId);

        EmitWorklist(new WorklistResult
        {
            RootId = rootId,
            ItemsWalked = walked.Count,
            HasConflicts = false,
            Conflicts = Array.Empty<EdgesCheckConflict>(),
            Waves = waves,
        }, json);
        return ExitCodes.Success;
    }

    /// <summary>
    /// BFS the plan tree from <paramref name="rootId"/>, returning a flat
    /// list of (item, parent id, knownInTwig) tuples in deterministic
    /// (id-ascending) order. The root must exist in the twig cache —
    /// the new edges-aware verb cannot derive a requirement set without
    /// the type, so a missing root surfaces as <c>root_not_found</c>
    /// rather than the legacy "unknown" placeholder.
    /// </summary>
    /// <remarks>
    /// We deduplicate ids defensively even though the twig cache treats
    /// children as a strict tree — the verb is read-only and a malformed
    /// cache should not produce duplicate items in the worklist.
    /// </remarks>
    private async Task<List<WalkedItem>> WalkSubtreeAsync(
        int rootId,
        CancellationToken ct)
    {
        var rootItem = await _repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (rootItem is null)
        {
            throw new CollectionFailureException(
                $"Run root {rootId} not found in twig cache.",
                "root_not_found");
        }

        var walked = new List<WalkedItem>
        {
            new(rootItem, ParentItemId: 0),
        };
        var seen = new HashSet<int> { rootId };

        // Standard BFS — children-by-parent, ascending id within each level.
        var currentLevel = new List<int> { rootId };
        while (currentLevel.Count > 0)
        {
            var nextLevel = new List<int>();
            foreach (var parentId in currentLevel)
            {
                ct.ThrowIfCancellationRequested();
                var children = await _repository.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
                foreach (var child in children.OrderBy(c => c.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    walked.Add(new WalkedItem(child, parentId));
                    nextLevel.Add(child.Id);
                }
            }
            currentLevel = nextLevel;
        }

        return walked;
    }

    /// <summary>
    /// Resolves <see cref="ResolvedRequirementInputs"/>, derives each
    /// item's <see cref="RequirementSet"/>, applies the execution-mode
    /// injector, and packs <see cref="EdgeGraphInput"/>s. Identical to
    /// the per-item composition used by <c>edges check</c>; centralizing
    /// here would couple the two verbs and is deferred until a third
    /// verb needs the same composition.
    /// </summary>
    private async Task<List<EdgeGraphInput>> BuildEdgeGraphInputsAsync(
        IReadOnlyList<WalkedItem> walked,
        CancellationToken ct)
    {
        var inputs = new List<EdgeGraphInput>(walked.Count);
        foreach (var w in walked)
        {
            ct.ThrowIfCancellationRequested();
            var typeName = w.Item.Type.Value ?? "";
            if (string.IsNullOrEmpty(typeName) || !_processConfig.Types.TryGetValue(typeName, out var typeConfig))
            {
                throw new CollectionFailureException(
                    $"Type '{typeName}' (item {w.Item.Id}) not found in process config.",
                    "type_unknown");
            }

            // Per-item facet override (closed-loop PR #7): architect-declared
            // apex_facets surface as a polyphony:facets=... tag. Replaces
            // type-config facets when present.
            var overrideFacets = ExtractFacetOverride(w.Item);

            var children = await _repository.GetChildrenAsync(w.Item.Id, ct).ConfigureAwait(false);
            var resolved = RequirementInputResolver.Resolve(typeConfig, children.Count, overrideFacets);
            var derivation = RequirementSetDeriver.Derive(
                resolved.Facets,
                resolved.Decomposable,
                resolved.FacetOrder,
                resolved.ActionableExecutor);

            if (!derivation.IsValid || derivation.Set is null)
            {
                throw new CollectionFailureException(
                    $"Derivation failed for item {w.Item.Id}: " + string.Join("; ", derivation.Errors),
                    "derivation_failed");
            }

            var injected = ExecutionModeInjector.Inject(derivation.Set, resolved.ExecutionMode);
            inputs.Add(new EdgeGraphInput(w.Item.Id, w.ParentItemId, injected));
        }
        return inputs;
    }

    private static IReadOnlyList<string>? ExtractFacetOverride(WorkItem item)
    {
        item.Fields.TryGetValue("System.Tags", out var raw);
        var tags = Polyphony.Tagging.TagSet.Parse(raw);
        var parsed = FacetTagParser.TryExtract(tags);
        if (parsed is null) return null;
        if (!parsed.IsValid)
        {
            throw new CollectionFailureException(
                $"Item {item.Id} has malformed polyphony:facets tag — unknown facet(s): {string.Join(", ", parsed.UnknownFacets)}.",
                "facet_override_invalid");
        }
        return parsed.Facets.Count == 0 ? null : parsed.Facets;
    }

    /// <summary>
    /// Projects <see cref="EdgeGraph.ToWaves"/> output (item ids only)
    /// into <see cref="WorklistWave"/>s carrying full per-item manifest
    /// metadata.
    /// </summary>
    private static IReadOnlyList<WorklistWave> ProjectWaves(
        IReadOnlyList<EdgeGraphWave> waves,
        IReadOnlyList<WalkedItem> walked,
        RunManifest manifest,
        int rootId)
    {
        var parentById = new Dictionary<int, int>(walked.Count);
        foreach (var w in walked)
        {
            parentById[w.Item.Id] = w.ParentItemId;
        }

        var projected = new List<WorklistWave>(waves.Count);
        foreach (var wave in waves)
        {
            var items = new List<WorklistItem>(wave.ItemIds.Count);
            foreach (var itemId in wave.ItemIds)
            {
                var parentItemId = parentById.TryGetValue(itemId, out var pid) ? pid : 0;
                items.Add(BuildItem(itemId, parentItemId, manifest, rootId));
            }
            projected.Add(new WorklistWave(wave.WaveIndex, items));
        }
        return projected;
    }

    /// <summary>
    /// Build a <see cref="WorklistItem"/> by joining the in-tree id /
    /// parent with the manifest's plan-PR ledger and generation map.
    /// </summary>
    private static WorklistItem BuildItem(
        int itemId,
        int parentItemId,
        RunManifest manifest,
        int rootId)
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

        // Items reaching this point are by construction known in twig
        // (the walk failed fast on a missing root and only enumerates
        // children that the cache produced).
        var status = latest is not null ? "merged" : "pending";
        var planPrNumber = latest?.PrNumber;

        return new WorklistItem(
            ItemId: itemId,
            ParentItemId: parentItemId,
            PlanStatus: status,
            PlanPrNumber: planPrNumber,
            CurrentGeneration: generation);
    }

    private static WorklistResult EmptyResult(int rootId, string error, string errorCode) =>
        new()
        {
            RootId = rootId,
            ItemsWalked = 0,
            HasConflicts = false,
            Conflicts = Array.Empty<EdgesCheckConflict>(),
            Waves = Array.Empty<WorklistWave>(),
            Error = error,
            ErrorCode = errorCode,
        };

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
    /// worklist: root=100  items=3  waves=2
    ///   wave 0:
    ///     item 100  parent=0    status=merged   pr=#42  generation=1
    ///   wave 1:
    ///     item 250  parent=100  status=pending  generation=0
    ///     item 310  parent=100  status=pending  generation=0
    /// </code>
    /// On conflicts, the body is replaced with a short conflicts block.
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

        sb.Append("  items=").Append(result.ItemsWalked.ToString(CultureInfo.InvariantCulture));
        sb.Append("  waves=").Append(result.Waves.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (result.HasConflicts)
        {
            sb.Append("  conflicts=").Append(result.Conflicts.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
            foreach (var c in result.Conflicts)
            {
                sb.Append("    ").Append(c.Kind).Append(": ").AppendLine(c.Description);
            }
            return sb.ToString().TrimEnd();
        }

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

    /// <summary>
    /// Snapshot of one work item enumerated by the BFS walk together
    /// with its parent id (0 for the run root). Used as the carrier
    /// between the walk and the edge-graph input builder.
    /// </summary>
    private sealed record WalkedItem(WorkItem Item, int ParentItemId);

    /// <summary>
    /// Internal control-flow exception used to short-circuit the BFS walk
    /// or per-item derivation when the verb cannot continue. Carries the
    /// routing-style <see cref="Code"/> the verb surfaces in the JSON
    /// envelope. Mirrors the same pattern in <c>EdgesCommands.Check</c>.
    /// </summary>
    private sealed class CollectionFailureException(string message, string code) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
