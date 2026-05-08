using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Sdlc;
using Twig.Domain.Aggregates;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony edges check</c> — walks a work item subtree, derives
/// each item's <see cref="RequirementSet"/>, builds a single
/// <see cref="EdgeGraph"/>, and emits any conflicts as a routable JSON
/// envelope.
///
/// <para>Routing-style verb: ALWAYS exits <see cref="ExitCodes.Success"/>;
/// the workflow gates on the envelope's <c>has_conflicts</c> boolean
/// and <c>conflicts[]</c> array. Errors that prevent producing a
/// graph (work item missing, type unknown, derivation failure) surface
/// as <c>error</c> + <c>error_code</c> in the same envelope.</para>
///
/// <para>Tree-walk semantics mirror <see cref="WorklistCommands"/>'
/// BFS: ascending-id child order for determinism, dedup defensively.
/// Unlike worklist build, we produce a flat list of items rather than
/// wave groupings — conflict detection happens at the merged-graph
/// level, not per wave.</para>
/// </summary>
public sealed partial class EdgesCommands
{
    /// <summary>
    /// Build the edge graph for a work item subtree and surface
    /// conflicts.
    /// </summary>
    /// <param name="workItem">Run-root work item id (positional, required).
    /// The verb walks the subtree rooted at this id.</param>
    /// <param name="depth">Optional max walk depth. Root sits at depth 0
    /// — so <c>--depth 1</c> walks only the root and its immediate
    /// children. Default (<c>0</c>) means unlimited.</param>
    /// <param name="render">Output mode. <c>json</c> (default) emits the
    /// JSON envelope on stdout. <c>text</c> additionally renders a
    /// Markdown conflict report to stderr — stdout still carries the
    /// JSON so workflow consumers and output_map readers stay stable.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check")]
    [VerbResult(typeof(EdgesCheckResult))]
    public async Task<int> Check(
        [Argument] int workItem,
        int depth = 0,
        string render = "json",
        CancellationToken ct = default)
    {
        if (workItem <= 0)
        {
            Emit(EmptyResult(workItem, "work_item_id must be positive", "invalid_argument"), render);
            return ExitCodes.Success;
        }

        if (depth < 0)
        {
            Emit(EmptyResult(workItem, $"--depth must be >= 0 (got {depth})", "invalid_argument"), render);
            return ExitCodes.Success;
        }

        List<EdgeGraphInput> inputs;
        try
        {
            inputs = await CollectInputsAsync(workItem, depth, ct).ConfigureAwait(false);
        }
        catch (CollectionFailureException ex)
        {
            Emit(EmptyResult(workItem, ex.Message, ex.Code), render);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Emit(EmptyResult(workItem, ex.Message, "cache_error"), render);
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
            Emit(EmptyResult(workItem, ex.Message, "graph_invalid"), render);
            return ExitCodes.Success;
        }

        var conflicts = graph.Conflicts.Select(c => new EdgesCheckConflict
        {
            Kind = c.Kind,
            Description = c.Description,
            ContributingEdges = c.ContributingEdges,
        }).ToArray();

        var result = new EdgesCheckResult
        {
            WorkItemId = workItem,
            ItemsWalked = inputs.Count,
            EdgesTotal = graph.Edges.Count,
            HasConflicts = conflicts.Length > 0,
            Conflicts = conflicts,
        };

        Emit(result, render);
        return ExitCodes.Success;
    }

    /// <summary>
    /// BFS walk from the root, deriving each item's <see cref="RequirementSet"/>
    /// and packing <see cref="EdgeGraphInput"/>s. Items past <paramref name="maxDepth"/>
    /// (when non-zero) are excluded along with their descendants.
    /// </summary>
    /// <remarks>
    /// We track parent ids explicitly per BFS level rather than reading
    /// <see cref="WorkItem.ParentId"/> off each child, so the graph
    /// reflects the in-scope tree topology even if the cache disagrees
    /// with the parent walk.
    /// </remarks>
    private async Task<List<EdgeGraphInput>> CollectInputsAsync(
        int rootId,
        int maxDepth,
        CancellationToken ct)
    {
        var rootItem = await _repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (rootItem is null)
        {
            throw new CollectionFailureException(
                $"Work item {rootId} not found.",
                "work_item_not_found");
        }

        var inputs = new List<EdgeGraphInput>();
        var seen = new HashSet<int> { rootId };
        inputs.Add(await DeriveInputAsync(rootItem, parentItemId: 0, ct).ConfigureAwait(false));

        // BFS with explicit depth tracking. levelDepth=0 corresponds to the
        // root; we expand into level=1, level=2, … up to maxDepth (when set).
        var currentLevel = new List<int> { rootId };
        var levelDepth = 0;

        while (currentLevel.Count > 0)
        {
            if (maxDepth > 0 && levelDepth >= maxDepth) break;

            var nextLevel = new List<int>();
            foreach (var parentId in currentLevel)
            {
                ct.ThrowIfCancellationRequested();
                var children = await _repository.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
                foreach (var child in children.OrderBy(c => c.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    inputs.Add(await DeriveInputAsync(child, parentItemId: parentId, ct).ConfigureAwait(false));
                    nextLevel.Add(child.Id);
                }
            }
            currentLevel = nextLevel;
            levelDepth++;
        }

        return inputs;
    }

    private async Task<EdgeGraphInput> DeriveInputAsync(
        WorkItem item,
        int parentItemId,
        CancellationToken ct)
    {
        var typeName = item.Type.Value ?? "";
        if (string.IsNullOrEmpty(typeName) || !_processConfig.Types.TryGetValue(typeName, out var typeConfig))
        {
            throw new CollectionFailureException(
                $"Type '{typeName}' (item {item.Id}) not found in process config.",
                "type_unknown");
        }

        var children = await _repository.GetChildrenAsync(item.Id, ct).ConfigureAwait(false);
        var resolved = RequirementInputResolver.Resolve(typeConfig, children.Count);
        var derivation = RequirementSetDeriver.Derive(
            typeConfig.Facets,
            resolved.Decomposable,
            resolved.FacetOrder,
            resolved.ActionableExecutor);

        if (!derivation.IsValid || derivation.Set is null)
        {
            throw new CollectionFailureException(
                $"Derivation failed for item {item.Id}: " + string.Join("; ", derivation.Errors),
                "derivation_failed");
        }

        return new EdgeGraphInput(item.Id, parentItemId, derivation.Set);
    }

    private static EdgesCheckResult EmptyResult(int workItemId, string error, string errorCode) =>
        new()
        {
            WorkItemId = workItemId,
            ItemsWalked = 0,
            EdgesTotal = 0,
            HasConflicts = false,
            Conflicts = Array.Empty<EdgesCheckConflict>(),
            Error = error,
            ErrorCode = errorCode,
        };

    private static void Emit(EdgesCheckResult result, string render)
    {
        // stdout: JSON envelope, always.
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.EdgesCheckResult));

        // stderr: Markdown conflict report, only when --render text.
        if (string.Equals(render, "text", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.Write(RenderMarkdown(result));
        }
    }

    /// <summary>
    /// Renders the Markdown conflict report described in the Phase 7 PR #3
    /// brief. Stable shape so workflow output_map consumers (and humans
    /// reviewing a stuck dispatch) can grep for the headings.
    /// </summary>
    private static string RenderMarkdown(EdgesCheckResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Edge Conflict Report — work item ")
          .Append(r.WorkItemId.ToString(CultureInfo.InvariantCulture))
          .AppendLine().AppendLine();

        if (r.Error is not null)
        {
            sb.Append("**Error:** ").Append(r.Error);
            if (!string.IsNullOrEmpty(r.ErrorCode))
            {
                sb.Append(" (").Append(r.ErrorCode).Append(')');
            }
            sb.AppendLine();
            return sb.ToString();
        }

        sb.Append("**Items walked:** ").Append(r.ItemsWalked.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("**Edges:** ").Append(r.EdgesTotal.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("**Conflicts:** ").Append(r.Conflicts.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();

        if (r.Conflicts.Count == 0) return sb.ToString();

        sb.AppendLine().AppendLine("## Conflicts").AppendLine();
        foreach (var c in r.Conflicts)
        {
            sb.Append("### ").Append(FormatConflictHeading(c)).AppendLine().AppendLine();
            sb.AppendLine("Contributing edges:");
            foreach (var e in c.ContributingEdges)
            {
                sb.Append("- (").Append(e.PrerequisiteItemId.ToString(CultureInfo.InvariantCulture))
                  .Append(", ").Append(e.PrerequisiteKind).Append(") → (")
                  .Append(e.DependentItemId.ToString(CultureInfo.InvariantCulture))
                  .Append(", ").Append(e.DependentKind).Append(") [")
                  .Append(e.Source).AppendLine("]");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatConflictHeading(EdgesCheckConflict c)
    {
        // Derive a friendly heading from the conflict description.
        // For cycles, the description already carries "Cycle detected: 100 -> 200 -> 100";
        // re-shape it as "Cycle: 100 → 200 → 100" for the Markdown header.
        if (string.Equals(c.Kind, EdgeConflictKind.Cycle, StringComparison.Ordinal)
            && c.Description.StartsWith("Cycle detected: ", StringComparison.Ordinal))
        {
            var path = c.Description["Cycle detected: ".Length..].Replace("->", "→");
            return "Cycle: " + path;
        }
        return $"{c.Kind}: {c.Description}";
    }

    /// <summary>
    /// Internal control-flow exception used to short-circuit the BFS walk
    /// when an item cannot be derived. Carries the routing-style
    /// <see cref="Code"/> the verb surfaces in the JSON envelope.
    /// </summary>
    private sealed class CollectionFailureException(string message, string code) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
