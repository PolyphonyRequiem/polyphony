using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Routing;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony branch next-impl</c>: select the next implementable item
/// in a merge group and transition it to its in-progress state.
/// Migrated from <c>scripts/impl-router.ps1</c>.
/// </summary>
public sealed partial class BranchCommands
{
    /// <summary>
    /// Picks the next non-terminal implementable item in the named merge
    /// group, transitions it via <c>begin_implementation</c>, and emits
    /// the branch name + workspace metadata the workflow needs to start
    /// work.
    /// </summary>
    /// <param name="workItem">ADO work item ID — root of the hierarchy.</param>
    /// <param name="pgName">Merge-group name (e.g. "PG-1"). Either this or pg-number is required. Operator-facing flag name preserved as <c>--pg-name</c> until the workflow rewire PR ships.</param>
    /// <param name="pgNumber">Merge-group number (e.g. 1). Convenience for callers that track merge groups as ints.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("next-impl")]
    public async Task<int> NextImpl(
        int workItem,
        string pgName = "",
        int pgNumber = 0,
        CancellationToken ct = default)
    {
        var resolvedMergeGroup = string.IsNullOrEmpty(pgName) && pgNumber > 0
            ? $"PG-{pgNumber}"
            : pgName;

        if (string.IsNullOrEmpty(resolvedMergeGroup))
        {
            EmitNextImpl(EmptyNextImplResult("Either --pg-name or --pg-number must be provided.", "", ""));
            return ExitCodes.Success;
        }

        BranchNextImplResult result;
        try
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var hierarchy = await walker.WalkAsync(workItem, maxDepth: 3, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                EmitNextImpl(EmptyNextImplResult($"Work item {workItem} not found", resolvedMergeGroup,
                    await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false)));
                return ExitCodes.Success;
            }

            // Build a parent-aware flat list so we can walk back up to find
            // the nearest plannable ancestor — the legacy script uses an
            // _parent property attached to each node; we model it as a
            // (node, parent) tuple instead.
            var nodes = FlattenWithParents(hierarchy).ToList();
            var implementable = nodes.Where(n => n.Node.Facets.Contains("implementable")).ToList();

            // Same fallback ladder as impl-router.ps1:
            //   1. items directly tagged with the merge group
            //   2. items whose parent container is tagged with the merge group
            //   3. issue-as-task: plannable+implementable, tagged, no children
            //   4. all implementable items
            var candidates = implementable
                .Where(n => string.Equals(ExtractLegacyPgTag(n.Node.Tags), resolvedMergeGroup, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                var mergeGroupContainerIds = nodes
                    .Where(n => n.Node.Facets.Contains("plannable")
                        && string.Equals(ExtractLegacyPgTag(n.Node.Tags), resolvedMergeGroup, StringComparison.Ordinal))
                    .Select(n => n.Node.WorkItemId)
                    .ToHashSet();
                candidates = implementable
                    .Where(n => n.Parent is not null && mergeGroupContainerIds.Contains(n.Parent.Node.WorkItemId))
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = nodes
                    .Where(n =>
                        n.Node.Facets.Contains("plannable")
                        && n.Node.Facets.Contains("implementable")
                        && string.Equals(ExtractLegacyPgTag(n.Node.Tags), resolvedMergeGroup, StringComparison.Ordinal)
                        && (n.Node.Children is null || n.Node.Children.Length == 0))
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = implementable;
            }

            var nonTerminal = candidates.Where(n => !IsTerminalCategory(n.Node.State)).ToList();
            var workspace = await ResolveAdoWorkspaceAsync(ct).ConfigureAwait(false);

            if (nonTerminal.Count == 0)
            {
                result = new BranchNextImplResult
                {
                    Action = "all_items_done",
                    PrimaryId = 0,
                    PrimaryTitle = "",
                    PrimaryType = "",
                    ContainerId = 0,
                    ContainerTitle = "",
                    ContainerType = "",
                    RemainingCount = 0,
                    CurrentMergeGroup = resolvedMergeGroup,
                    BranchName = "",
                    AdoWorkspace = workspace,
                };
                EmitNextImpl(result);
                return ExitCodes.Success;
            }

            var next = nonTerminal[0];
            var nextItem = await repository.GetByIdAsync(next.Node.WorkItemId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Hierarchy returned item {next.Node.WorkItemId} but cache lookup failed");
            var children = await repository.GetChildrenAsync(next.Node.WorkItemId, ct).ConfigureAwait(false);
            var outcome = validator.Validate(nextItem, "begin_implementation", children);
            string targetState;
            switch (outcome)
            {
                case ValidTransition v:
                    targetState = v.TargetState;
                    break;
                case InvalidTransition iv:
                    throw new InvalidOperationException(
                        $"Cannot start item {next.Node.WorkItemId} (event=begin_implementation): {iv.Message}");
                default:
                    throw new InvalidOperationException(
                        $"Cannot start item {next.Node.WorkItemId} (event=begin_implementation): validator returned null");
            }

            await twig.SetActiveAsync(next.Node.WorkItemId, ct).ConfigureAwait(false);
            await twig.SetStateAsync(targetState, ct).ConfigureAwait(false);

            // Walk up to find the nearest plannable ancestor (the container).
            var (containerId, containerTitle, containerType) = FindNearestPlannableAncestorWithType(next);

            // Resolve branch name: prefer config-driven workspace_hint
            // merge-group-branch pattern (legacy JSON wire key
            // "pg_branch"), fall back to feature/{rootId}-{slug-of-mg}.
            var rootItem = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
            var hint = rootItem is not null ? BranchNameResolver.Resolve(processConfig, rootItem) : null;

            var branchName = await ResolveBranchNameAsync(hint, resolvedMergeGroup, workItem, ct).ConfigureAwait(false);

            result = new BranchNextImplResult
            {
                Action = "implement_item",
                PrimaryId = next.Node.WorkItemId,
                PrimaryTitle = next.Node.Title,
                PrimaryType = next.Node.Type,
                ContainerId = containerId,
                ContainerTitle = containerTitle,
                ContainerType = containerType,
                RemainingCount = nonTerminal.Count,
                CurrentMergeGroup = resolvedMergeGroup,
                BranchName = branchName,
                AdoWorkspace = workspace,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = EmptyNextImplResult($"Error routing next task: {ex.Message}",
                resolvedMergeGroup, await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
        }

        EmitNextImpl(result);
        return ExitCodes.Success;
    }

    private async Task<string> ResolveBranchNameAsync(
        WorkspaceHint? hint, string mergeGroupName, int rootId, CancellationToken ct)
    {
        string expected;
        if (hint is { MergeGroupBranch: { Length: > 0 } template })
        {
            // Substitute {n} OR {pg} (the merge-group number) into the
            // configured branch template — accept both conventions used
            // in the wild. Legacy template tokens preserved until the
            // workflow rewire PR ships.
            var num = ExtractLegacyPgNumber(mergeGroupName);
            expected = template
                .Replace("{n}", num, StringComparison.OrdinalIgnoreCase)
                .Replace("{pg}", num, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var slug = Slugify(mergeGroupName);
            expected = $"feature/{rootId}-{slug}";
            if (expected.Length > 60) expected = expected[..60];
        }

        // If we're already on the expected branch, return it; else return
        // the expected name so the workflow can switch/create it.
        var current = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false) ?? "";
        return string.Equals(current, expected, StringComparison.Ordinal) ? current : expected;
    }

    /// <summary>
    /// Parses the legacy <c>PG-N</c> merge-group name format to extract
    /// the numeric suffix. "Legacy" because the planner-emitted format
    /// is scheduled to flip in the prompt-migration PR; until then this
    /// reader only accepts <c>PG-N</c>.
    /// </summary>
    private static string ExtractLegacyPgNumber(string mergeGroupName)
    {
        if (mergeGroupName.StartsWith("PG-", StringComparison.Ordinal)
            && int.TryParse(mergeGroupName[3..], out var n))
        {
            return n.ToString();
        }
        return "1";
    }

    private static (int Id, string Title, string Type) FindNearestPlannableAncestorWithType(NodeWithParent start)
    {
        // Walk up the parent chain stored on each NodeWithParent. The
        // canonical 3-deep epic→container→item tree resolves on the first hop
        // (container is plannable); a deeper tree continues until we exhaust
        // the chain.
        var current = start.Parent;
        while (current is not null)
        {
            if (current.Node.Facets.Contains("plannable"))
            {
                return (current.Node.WorkItemId, current.Node.Title, current.Node.Type);
            }
            current = current.Parent;
        }
        return (0, "", "");
    }

    private static IEnumerable<NodeWithParent> FlattenWithParents(HierarchyResult node, NodeWithParent? parent = null)
    {
        var entry = new NodeWithParent(node, parent);
        yield return entry;
        if (node.Children is null) yield break;
        foreach (var child in node.Children)
        {
            foreach (var sub in FlattenWithParents(child, entry))
            {
                yield return sub;
            }
        }
    }

    private sealed record NodeWithParent(HierarchyResult Node, NodeWithParent? Parent);

    private static BranchNextImplResult EmptyNextImplResult(string error, string mergeGroup, string adoWorkspace) => new()
    {
        Action = "error",
        PrimaryId = 0,
        PrimaryTitle = "",
        PrimaryType = "",
        ContainerId = 0,
        ContainerTitle = "",
        ContainerType = "",
        RemainingCount = 0,
        CurrentMergeGroup = mergeGroup,
        BranchName = "",
        AdoWorkspace = adoWorkspace,
        Error = error,
    };

    private static void EmitNextImpl(BranchNextImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchNextImplResult));
}

