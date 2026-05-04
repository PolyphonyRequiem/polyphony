using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Polyphony.Infrastructure.Processes;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;

namespace Polyphony.Commands;

/// <summary>
/// Branch-lifecycle verbs: load-tree, route, next-task, check-deps,
/// close-scope. Migrated from the corresponding <c>scripts/*.ps1</c>
/// helpers so the workflow YAMLs can shell out to <c>polyphony branch
/// &lt;verb&gt;</c> instead of pwsh script files.
///
/// Routing-style verbs ALWAYS exit 0; callers route on the JSON payload
/// (see <c>docs/decisions/polyphony-verb-migration.md</c>).
/// </summary>
public sealed class BranchCommands(ITwigClient twig)
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
}
