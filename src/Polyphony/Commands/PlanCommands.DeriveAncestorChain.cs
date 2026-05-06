using System.Text.Json;
using ConsoleAppFramework;
using Twig.Domain.Aggregates;

namespace Polyphony.Commands;

/// <summary>
/// Phase 3 P7a: derive the plan-tree ancestor chain for a single work item.
/// Routing-style verb consumed by <c>plan-level.yaml</c> to compute the
/// <c>--parent-item-id</c> and <c>--ancestor-ids</c> flags for the plan-PR
/// verbs (<c>branch ensure-plan</c>, <c>pr open-plan-pr</c>, <c>pr merge-plan-pr</c>).
/// Walks <see cref="WorkItem.ParentId"/> from <paramref name="itemId"/> up
/// to <paramref name="rootId"/>; emits an error if the item is not a
/// descendant of the root, the chain is broken, or a cycle is detected.
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Maximum ancestors to walk before declaring a cycle. The plan-tree
    /// recursion is independently capped at <c>max_depth</c> in the workflow,
    /// so 50 is comfortably above any realistic hierarchy.
    /// </summary>
    private const int AncestorWalkLimit = 50;

    /// <summary>
    /// Walks the parent chain of <paramref name="itemId"/> up to (but not
    /// past) <paramref name="rootId"/> and emits <see cref="PlanDeriveAncestorChainResult"/>.
    /// Always exits 0; consumers branch on <c>error</c> or the structured fields.
    /// </summary>
    /// <param name="rootId">Run-root work-item id (positive).</param>
    /// <param name="itemId">Item being planned (positive). May equal <paramref name="rootId"/> for the root plan.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("derive-ancestor-chain")]
    public async Task<int> DeriveAncestorChain(
        int rootId,
        int itemId,
        CancellationToken ct = default)
    {
        if (rootId <= 0)
        {
            EmitChainError(rootId, itemId, $"--root-id must be positive (got {rootId})");
            return ExitCodes.Success;
        }

        if (itemId <= 0)
        {
            EmitChainError(rootId, itemId, $"--item-id must be positive (got {itemId})");
            return ExitCodes.Success;
        }

        if (itemId == rootId)
        {
            EmitChain(new PlanDeriveAncestorChainResult
            {
                RootId = rootId,
                ItemId = itemId,
                IsRootPlan = true,
                ParentItemId = null,
                AncestorIds = string.Empty,
                AncestorChain = [],
                Depth = 0,
            });
            return ExitCodes.Success;
        }

        // Walk parent chain. We need both the immediate parent (for
        // --parent-item-id) and the full chain (for --ancestor-ids).
        var item = await repository.GetByIdAsync(itemId, ct);
        if (item is null)
        {
            EmitChainError(rootId, itemId, $"Work item {itemId} not found");
            return ExitCodes.Success;
        }

        var ancestors = new List<int>();
        var visited = new HashSet<int> { itemId };
        var cursor = item;

        for (var step = 0; step < AncestorWalkLimit; step++)
        {
            if (cursor.ParentId is not { } parentId)
            {
                EmitChainError(rootId, itemId,
                    $"Work item {itemId} is not a descendant of root {rootId} (parent chain ends at {cursor.Id} with no parent)");
                return ExitCodes.Success;
            }

            if (!visited.Add(parentId))
            {
                EmitChainError(rootId, itemId,
                    $"Cycle detected in parent chain at work item {parentId}");
                return ExitCodes.Success;
            }

            if (parentId == rootId)
            {
                // Reached the root. Emit the chain.
                ancestors.Add(rootId);
                EmitSuccess(rootId, itemId, ancestors);
                return ExitCodes.Success;
            }

            ancestors.Add(parentId);

            var parent = await repository.GetByIdAsync(parentId, ct);
            if (parent is null)
            {
                EmitChainError(rootId, itemId,
                    $"Ancestor work item {parentId} not found (broken parent chain)");
                return ExitCodes.Success;
            }
            cursor = parent;
        }

        EmitChainError(rootId, itemId,
            $"Ancestor walk exceeded limit of {AncestorWalkLimit} steps; cycle suspected");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Renders the success payload. The last entry in <paramref name="ancestorsIncludingRoot"/>
    /// is always the root id; immediate parent is first.
    /// </summary>
    private static void EmitSuccess(int rootId, int itemId, IReadOnlyList<int> ancestorsIncludingRoot)
    {
        // Replace the trailing root id with the literal "root" token. The
        // pr open-plan-pr verb's --ancestor-ids contract is "immediate parent
        // first, ending in 'root'".
        var chain = new string[ancestorsIncludingRoot.Count];
        for (var i = 0; i < ancestorsIncludingRoot.Count - 1; i++)
        {
            chain[i] = ancestorsIncludingRoot[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        chain[^1] = "root";

        // parent_item_id is null when the item is a direct child of root
        // (chain == ["root"]); otherwise it's the immediate parent's id.
        int? parentItemId = ancestorsIncludingRoot.Count == 1
            ? null
            : ancestorsIncludingRoot[0];

        EmitChain(new PlanDeriveAncestorChainResult
        {
            RootId = rootId,
            ItemId = itemId,
            IsRootPlan = false,
            ParentItemId = parentItemId,
            AncestorIds = string.Join(",", chain),
            AncestorChain = chain,
            Depth = chain.Length,
        });
    }

    private static void EmitChainError(int rootId, int itemId, string message)
    {
        EmitChain(new PlanDeriveAncestorChainResult
        {
            RootId = rootId,
            ItemId = itemId,
            IsRootPlan = rootId == itemId,
            ParentItemId = null,
            AncestorIds = string.Empty,
            AncestorChain = [],
            Depth = 0,
            Error = message,
        });
    }

    private static void EmitChain(PlanDeriveAncestorChainResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanDeriveAncestorChainResult));
    }
}
