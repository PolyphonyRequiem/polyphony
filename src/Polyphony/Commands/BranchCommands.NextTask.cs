using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Routing;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony branch next-task</c>: select the next implementable item
/// in a PG and transition it to its in-progress state.
/// Migrated from <c>scripts/task-router.ps1</c>.
/// </summary>
public sealed partial class BranchCommands
{
    /// <summary>
    /// Picks the next non-terminal implementable item in the named PG,
    /// transitions it via <c>begin_implementation</c>, and emits the
    /// branch name + workspace metadata the workflow needs to start work.
    /// </summary>
    /// <param name="workItem">ADO work item ID — root of the hierarchy.</param>
    /// <param name="pgName">PG name (e.g. "PG-1"). Either this or pg-number is required.</param>
    /// <param name="pgNumber">PG number (e.g. 1). Convenience for callers that track PG as int.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("next-task")]
    public async Task<int> NextTask(
        int workItem,
        string pgName = "",
        int pgNumber = 0,
        CancellationToken ct = default)
    {
        var resolvedPg = string.IsNullOrEmpty(pgName) && pgNumber > 0
            ? $"PG-{pgNumber}"
            : pgName;

        if (string.IsNullOrEmpty(resolvedPg))
        {
            EmitNextTask(EmptyNextTaskResult("Either --pg-name or --pg-number must be provided.", "", ""));
            return ExitCodes.Success;
        }

        BranchNextTaskResult result;
        try
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var hierarchy = await walker.WalkAsync(workItem, maxDepth: 3, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                EmitNextTask(EmptyNextTaskResult($"Work item {workItem} not found", resolvedPg,
                    await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false)));
                return ExitCodes.Success;
            }

            // Build a parent-aware flat list so we can walk back up to find
            // the nearest plannable ancestor — the legacy script uses an
            // _parent property attached to each node; we model it as a
            // (node, parent) tuple instead.
            var nodes = FlattenWithParents(hierarchy).ToList();
            var implementable = nodes.Where(n => n.Node.Capabilities.Contains("implementable")).ToList();

            // Same fallback ladder as task-router.ps1:
            //   1. items directly tagged with the PG
            //   2. items whose parent container is tagged with the PG
            //   3. issue-as-task: plannable+implementable, tagged, no children
            //   4. all implementable items
            var candidates = implementable
                .Where(n => string.Equals(ExtractPgTag(n.Node.Tags), resolvedPg, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                var pgContainerIds = nodes
                    .Where(n => n.Node.Capabilities.Contains("plannable")
                        && string.Equals(ExtractPgTag(n.Node.Tags), resolvedPg, StringComparison.Ordinal))
                    .Select(n => n.Node.WorkItemId)
                    .ToHashSet();
                candidates = implementable
                    .Where(n => n.Parent is not null && pgContainerIds.Contains(n.Parent.Node.WorkItemId))
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                candidates = nodes
                    .Where(n =>
                        n.Node.Capabilities.Contains("plannable")
                        && n.Node.Capabilities.Contains("implementable")
                        && string.Equals(ExtractPgTag(n.Node.Tags), resolvedPg, StringComparison.Ordinal)
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
                result = new BranchNextTaskResult
                {
                    Action = "all_items_done",
                    PrimaryId = 0,
                    PrimaryTitle = "",
                    PrimaryType = "",
                    ContainerId = 0,
                    ContainerTitle = "",
                    ContainerType = "",
                    RemainingCount = 0,
                    CurrentPg = resolvedPg,
                    BranchName = "",
                    AdoWorkspace = workspace,
                };
                EmitNextTask(result);
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

            // Resolve branch name: prefer config-driven workspace_hint.pg_branch
            // pattern, fall back to feature/{rootId}-{slug-of-pg}.
            var rootItem = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
            var hint = rootItem is not null ? BranchNameResolver.Resolve(processConfig, rootItem) : null;

            var branchName = await ResolveBranchNameAsync(hint, resolvedPg, workItem, ct).ConfigureAwait(false);

            result = new BranchNextTaskResult
            {
                Action = "implement_item",
                PrimaryId = next.Node.WorkItemId,
                PrimaryTitle = next.Node.Title,
                PrimaryType = next.Node.Type,
                ContainerId = containerId,
                ContainerTitle = containerTitle,
                ContainerType = containerType,
                RemainingCount = nonTerminal.Count,
                CurrentPg = resolvedPg,
                BranchName = branchName,
                AdoWorkspace = workspace,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = EmptyNextTaskResult($"Error routing next task: {ex.Message}",
                resolvedPg, await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
        }

        EmitNextTask(result);
        return ExitCodes.Success;
    }

    private async Task<string> ResolveBranchNameAsync(
        WorkspaceHint? hint, string pgName, int rootId, CancellationToken ct)
    {
        string expected;
        if (hint is { PgBranch: { Length: > 0 } pgBranchTemplate })
        {
            // Substitute {n} OR {pg} (the PG number) into the configured
            // pg_branch template — accept both conventions used in the wild.
            var pgNum = ExtractPgNumber(pgName);
            expected = pgBranchTemplate
                .Replace("{n}", pgNum, StringComparison.OrdinalIgnoreCase)
                .Replace("{pg}", pgNum, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var slug = Slugify(pgName);
            expected = $"feature/{rootId}-{slug}";
            if (expected.Length > 60) expected = expected[..60];
        }

        // If we're already on the expected branch, return it; else return
        // the expected name so the workflow can switch/create it.
        var current = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false) ?? "";
        return string.Equals(current, expected, StringComparison.Ordinal) ? current : expected;
    }

    private static string ExtractPgNumber(string pgName)
    {
        if (pgName.StartsWith("PG-", StringComparison.Ordinal)
            && int.TryParse(pgName[3..], out var n))
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
            if (current.Node.Capabilities.Contains("plannable"))
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

    private static BranchNextTaskResult EmptyNextTaskResult(string error, string pg, string adoWorkspace) => new()
    {
        Action = "error",
        PrimaryId = 0,
        PrimaryTitle = "",
        PrimaryType = "",
        ContainerId = 0,
        ContainerTitle = "",
        ContainerType = "",
        RemainingCount = 0,
        CurrentPg = pg,
        BranchName = "",
        AdoWorkspace = adoWorkspace,
        Error = error,
    };

    private static void EmitNextTask(BranchNextTaskResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchNextTaskResult));
}
