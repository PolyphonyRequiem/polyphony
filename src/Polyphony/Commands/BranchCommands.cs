using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;

namespace Polyphony.Commands;

/// <summary>
/// Branch-lifecycle verbs: load-tree, route, next-impl, check-deps,
/// close-scope. Migrated from the corresponding <c>scripts/*.ps1</c>
/// helpers so the workflow YAMLs can shell out to <c>polyphony branch
/// &lt;verb&gt;</c> instead of pwsh script files.
///
/// Routing-style verbs ALWAYS exit 0; callers route on the JSON payload
/// (see <c>docs/decisions/polyphony-verb-migration.md</c>).
/// </summary>
public sealed partial class BranchCommands(
    ITwigClient twig,
    HierarchyWalker walker,
    IWorkItemRepository repository,
    TransitionValidator validator,
    IGitClient git,
    IGhClient gh,
    ProcessConfig processConfig)
{
    /// <summary>
    /// Check ADO predecessor links for blocking dependencies on a work item.
    /// Replaces <c>scripts/dependency-check.ps1</c>.
    /// </summary>
    /// <param name="workItem">ADO work item ID to check predecessor links for.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check-deps")]
    public async Task<int> CheckDeps(int workItem, CancellationToken ct = default)
    {
        BranchCheckDepsResult result;
        try
        {
            // Sync first so we don't route on stale data. Failures here are
            // recoverable — surface as 'not_blocked' with error=true and let
            // the workflow decide.
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var item = await twig.ShowAsync(workItem, ct).ConfigureAwait(false);
            if (item is null)
            {
                result = ErrorResult(workItem, $"Failed to fetch work item {workItem}");
                Emit(result);
                return ExitCodes.Success;
            }

            var predecessors = ExtractPredecessors(item);

            if (predecessors.Count == 0)
            {
                result = new BranchCheckDepsResult
                {
                    Blocked = false,
                    Status = "not_blocked",
                    WorkItemId = workItem,
                    BlockingItems = [],
                    ReadyCount = 0,
                    TotalCount = 0,
                    Message = "No predecessor links found",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var blocking = new List<BlockingItem>();
            foreach (var predId in predecessors)
            {
                var pred = await twig.ShowAsync(predId, ct).ConfigureAwait(false);
                if (pred is null)
                {
                    blocking.Add(new BlockingItem
                    {
                        Id = predId,
                        Title = "Unknown (failed to fetch)",
                        State = "Unknown",
                    });
                    continue;
                }

                var (state, title) = ExtractStateAndTitle(pred);
                if (!IsTerminal(state))
                {
                    blocking.Add(new BlockingItem
                    {
                        Id = predId,
                        Title = title,
                        State = string.IsNullOrEmpty(state) ? "Unknown" : state,
                    });
                }
            }

            var readyCount = predecessors.Count - blocking.Count;
            result = blocking.Count > 0
                ? new BranchCheckDepsResult
                {
                    Blocked = true,
                    Status = "blocked",
                    WorkItemId = workItem,
                    BlockingItems = blocking,
                    ReadyCount = readyCount,
                    TotalCount = predecessors.Count,
                    Message = $"{blocking.Count} predecessor(s) not in terminal state",
                }
                : new BranchCheckDepsResult
                {
                    Blocked = false,
                    Status = "not_blocked",
                    WorkItemId = workItem,
                    BlockingItems = [],
                    ReadyCount = predecessors.Count,
                    TotalCount = predecessors.Count,
                    Message = $"All {predecessors.Count} predecessor(s) are complete",
                };
        }
        catch (Exception ex)
        {
            result = ErrorResult(workItem, $"Error checking dependencies: {ex.Message}");
        }

        Emit(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Extracts predecessor work-item IDs from a twig <c>show</c> JSON payload.
    /// A relation is a predecessor if its <c>rel</c> is
    /// <c>System.LinkTypes.Dependency-Reverse</c> or its
    /// <c>attributes.name</c> equals <c>"Predecessor"</c>.
    /// </summary>
    private static IReadOnlyList<int> ExtractPredecessors(JsonNode item)
    {
        var ids = new List<int>();
        var relations = item["relations"];
        if (relations is not JsonArray arr)
        {
            return ids;
        }

        foreach (var rel in arr)
        {
            if (rel is null) continue;

            var relType = rel["rel"]?.GetValue<string>();
            var attrName = rel["attributes"]?["name"]?.GetValue<string>();

            var isPredecessor =
                string.Equals(relType, "System.LinkTypes.Dependency-Reverse", StringComparison.Ordinal)
                || string.Equals(attrName, "Predecessor", StringComparison.Ordinal);
            if (!isPredecessor) continue;

            // ID may come from a `url` (last segment) or a direct `id` field.
            var id = TryParseIdFromRelation(rel);
            if (id is int parsed)
            {
                ids.Add(parsed);
            }
        }
        return ids;
    }

    private static int? TryParseIdFromRelation(JsonNode rel)
    {
        var url = rel["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(url))
        {
            var lastSlash = url.LastIndexOf('/');
            if (lastSlash >= 0
                && int.TryParse(url[(lastSlash + 1)..], out var fromUrl))
            {
                return fromUrl;
            }
        }

        var direct = rel["id"];
        if (direct is not null && int.TryParse(direct.ToString(), out var fromId))
        {
            return fromId;
        }

        return null;
    }

    private static (string State, string Title) ExtractStateAndTitle(JsonNode item)
    {
        var fields = item["fields"];
        var state = fields?["System.State"]?.GetValue<string>() ?? string.Empty;
        var title = fields?["System.Title"]?.GetValue<string>() ?? string.Empty;
        return (state, title);
    }

    /// <summary>
    /// Type-agnostic terminal-state check via twig.Domain's
    /// <see cref="StateCategoryResolver"/>. Replaces the hardcoded
    /// <c>{Done, Removed, Closed}</c> set in the legacy script (P5
    /// violation flagged in the migration ADR).
    /// </summary>
    private static bool IsTerminal(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var category = StateCategoryResolver.Resolve(state, entries: null);
        return category == StateCategory.Completed || category == StateCategory.Removed;
    }

    private static BranchCheckDepsResult ErrorResult(int workItem, string message) => new()
    {
        Blocked = false,
        Status = "not_blocked",
        WorkItemId = workItem,
        BlockingItems = [],
        ReadyCount = 0,
        TotalCount = 0,
        Message = message,
        Error = true,
    };

    private static void Emit(BranchCheckDepsResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchCheckDepsResult));

    /// <summary>
    /// Close all non-terminal items in a merge-group scope by validating
    /// each transition against the process config and transitioning valid
    /// items to their target state. Replaces <c>scripts/scope-closer.ps1</c>.
    /// </summary>
    /// <param name="workItem">ADO work item ID — root of the hierarchy.</param>
    /// <param name="pgName">Merge-group name (e.g. "PG-1"). Either this or pg-number is required. Operator-facing flag name preserved as <c>--pg-name</c> until the workflow rewire PR ships.</param>
    /// <param name="pgNumber">Merge-group number (e.g. 1). Convenience for callers that track merge groups as ints.</param>
    /// <param name="prNumber">PR number associated with this scope closure (echoed in output).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("close-scope")]
    public async Task<int> CloseScope(
        int workItem,
        string pgName = "",
        int pgNumber = 0,
        int prNumber = 0,
        CancellationToken ct = default)
    {
        var resolvedMergeGroup = string.IsNullOrEmpty(pgName) && pgNumber > 0
            ? $"PG-{pgNumber}"
            : pgName;

        if (string.IsNullOrEmpty(resolvedMergeGroup))
        {
            EmitClose(EmptyClose("", prNumber, "Either --pg-name or --pg-number must be provided.", ""));
            return ExitCodes.Success;
        }

        BranchCloseScopeResult result;
        try
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var hierarchy = await walker.WalkAsync(workItem, maxDepth: 3, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                EmitClose(EmptyClose(resolvedMergeGroup, prNumber,
                    $"Work item {workItem} not found", await ResolveAdoWorkspaceAsync(ct).ConfigureAwait(false)));
                return ExitCodes.Success;
            }

            var allItems = Flatten(hierarchy).ToList();
            var mergeGroupItemIds = ItemsForMergeGroup(allItems, resolvedMergeGroup);
            var mergeGroupItems = allItems.Where(i => mergeGroupItemIds.Contains(i.WorkItemId)).ToList();

            // P5-compliant terminal check via twig.Domain (replaces hardcoded
            // 'Done' string in scope-closer.ps1:63).
            var nonTerminal = mergeGroupItems.Where(i => !IsTerminalCategory(i.State)).ToList();

            var closed = new List<ClosedItem>();
            var failed = new List<FailedClosure>();

            foreach (var node in nonTerminal)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, target, reason) = await ValidateAsync(node.WorkItemId, ct).ConfigureAwait(false);
                if (!ok || target is null)
                {
                    failed.Add(new FailedClosure { Id = node.WorkItemId, Title = node.Title, Reason = reason ?? "" });
                    continue;
                }

                try
                {
                    await twig.SetActiveAsync(node.WorkItemId, ct).ConfigureAwait(false);
                    await twig.SetStateAsync(target, ct).ConfigureAwait(false);
                    closed.Add(new ClosedItem { Id = node.WorkItemId, Title = node.Title, TargetState = target });
                }
                catch (Exception ex)
                {
                    failed.Add(new FailedClosure
                    {
                        Id = node.WorkItemId,
                        Title = node.Title,
                        Reason = $"Transition failed: {ex.Message}",
                    });
                }
            }

            result = new BranchCloseScopeResult
            {
                MergeGroupName = resolvedMergeGroup,
                PrNumber = prNumber,
                ClosedItems = closed,
                FailedClosures = failed,
                TotalClosed = closed.Count,
                TotalFailed = failed.Count,
                AdoWorkspace = await ResolveAdoWorkspaceAsync(ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = EmptyClose(resolvedMergeGroup, prNumber, $"Error closing scope: {ex.Message}",
                await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
        }

        EmitClose(result);
        return ExitCodes.Success;
    }

    private async Task<(bool Ok, string? TargetState, string? Reason)> ValidateAsync(int id, CancellationToken ct)
    {
        var item = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return (false, null, $"Work item {id} not found in cache");
        }
        var children = await repository.GetChildrenAsync(id, ct).ConfigureAwait(false);
        var outcome = validator.Validate(item, "implementation_complete", children);
        return outcome switch
        {
            ValidTransition v => (true, v.TargetState, v.Message),
            InvalidTransition iv => (false, iv.TargetState, iv.Message),
            _ => (false, null, "Validator returned null"),
        };
    }

    private async Task<string> ResolveAdoWorkspaceAsync(CancellationToken ct)
    {
        var org = await twig.GetConfigValueAsync("organization", ct).ConfigureAwait(false);
        var proj = await twig.GetConfigValueAsync("project", ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(proj) ? $"{org}/{proj}" : "";
    }

    private async Task<string> TryResolveAdoWorkspaceAsync(CancellationToken ct)
    {
        try { return await ResolveAdoWorkspaceAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    /// <summary>
    /// Group-by-merge-group logic from <c>scripts/lib/pg-helpers.ps1</c>:
    ///   - Parse the work item's tags for the first <c>PG-N</c> entry
    ///     (legacy tag format — see <see cref="ExtractLegacyPgTag"/>).
    ///   - Classify by facet: implementable items go to implementables,
    ///     plannable-with-children items go to containers.
    /// "Issue-as-task" (plannable+implementable with no children) is treated
    /// as implementable.
    /// </summary>
    private static HashSet<int> ItemsForMergeGroup(IEnumerable<HierarchyResult> items, string mergeGroup)
    {
        var ids = new HashSet<int>();
        foreach (var item in items)
        {
            var tag = ExtractLegacyPgTag(item.Tags);
            if (!string.Equals(tag, mergeGroup, StringComparison.Ordinal)) continue;
            ids.Add(item.WorkItemId);
        }
        return ids;
    }

    /// <summary>
    /// Parses the legacy <c>PG-N</c> tag format from a work item's tag
    /// string. Named "legacy" because the planner-emitted format is
    /// scheduled to flip in the prompt-migration PR; until then this
    /// reader only accepts <c>PG-N</c>.
    /// </summary>
    private static string? ExtractLegacyPgTag(string? tags)
    {
        if (string.IsNullOrEmpty(tags)) return null;
        foreach (var raw in tags.Split(';'))
        {
            var t = raw.Trim();
            if (t.StartsWith("PG-", StringComparison.Ordinal)
                && t.Length > 3
                && t[3..].All(char.IsDigit))
            {
                return t;
            }
        }
        return null;
    }

    private static IEnumerable<HierarchyResult> Flatten(HierarchyResult node)
    {
        yield return node;
        if (node.Children is null) yield break;
        foreach (var child in node.Children)
        {
            foreach (var sub in Flatten(child))
            {
                yield return sub;
            }
        }
    }

    private static bool IsTerminalCategory(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        var category = StateCategoryResolver.Resolve(state, entries: null);
        return category == StateCategory.Completed || category == StateCategory.Removed;
    }

    private static BranchCloseScopeResult EmptyClose(string mergeGroup, int pr, string error, string adoWorkspace) => new()
    {
        MergeGroupName = mergeGroup,
        PrNumber = pr,
        ClosedItems = [],
        FailedClosures = [],
        TotalClosed = 0,
        TotalFailed = 0,
        AdoWorkspace = adoWorkspace,
        Error = error,
    };

    private static void EmitClose(BranchCloseScopeResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchCloseScopeResult));
}

