using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony branch route</c>: classifies every PR group in the
/// hierarchy and reports the next action the workflow should take —
/// create_branch, submit_pr, or all_complete. Migrated from
/// <c>scripts/pg-router.ps1</c>.
/// </summary>
public sealed partial class BranchCommands
{
    /// <summary>
    /// Routes a work-item hierarchy to its next PR-group action.
    /// </summary>
    /// <param name="workItem">ADO work item ID — root of the hierarchy.</param>
    /// <param name="pgNumber">
    /// Optional PG number (e.g. 2) to scope routing to. When supplied,
    /// the named PG is selected as <c>current_pg</c> regardless of
    /// whether earlier PGs are still incomplete — required for parallel
    /// for_each dispatch where each invocation must route on its own PG.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("route")]
    public async Task<int> Route(int workItem, int pgNumber = 0, CancellationToken ct = default)
    {
        BranchRouteResult result;
        try
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var hierarchy = await walker.WalkAsync(workItem, maxDepth: 3, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                EmitRoute(EmptyRouteResult($"Work item {workItem} not found",
                    await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false)));
                return ExitCodes.Success;
            }

            var allItems = Flatten(hierarchy).ToList();
            var rootItem = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
            var hint = rootItem is not null ? BranchNameResolver.Resolve(processConfig, rootItem) : null;

            // Build PG groups (or a single fallback PG-1 when no tags exist).
            var groups = BuildRouteGroups(workItem, hierarchy, allItems, hint);

            // Resolve repo + remote branches + PR lists; degrade gracefully on failure.
            var repoSlug = await TryListRemoteSlugAsync(ct).ConfigureAwait(false);
            var remoteBranches = await TryListRemoteBranchesAsync(ct).ConfigureAwait(false);
            var mergedPrs = await TryListPrsAsync(repoSlug, "merged", ct).ConfigureAwait(false);
            var openPrs = await TryListPrsAsync(repoSlug, "open", ct).ConfigureAwait(false);

            var classified = ClassifyGroups(groups, allItems, remoteBranches, mergedPrs, openPrs);

            // PG scoping: parallel dispatch overrides "first non-completed".
            ClassifiedPg? current;
            if (pgNumber > 0)
            {
                current = classified
                    .FirstOrDefault(c => string.Equals(c.Group.Name, $"PG-{pgNumber}", StringComparison.Ordinal));
                if (current is not null && current.Completed && current.Action != "submit_pr")
                {
                    current = current with { Action = "all_complete" };
                }
            }
            else
            {
                current = classified.FirstOrDefault(c => !c.Completed);
            }

            var workspace = await ResolveAdoWorkspaceAsync(ct).ConfigureAwait(false);
            var completedPgs = classified.Where(c => c.Completed).Select(c => c.Group.Name).ToList();
            var remainingPgs = classified.Where(c => !c.Completed).Select(c => c.Group.Name).ToList();

            if (current is null)
            {
                result = new BranchRouteResult
                {
                    Action = "all_complete",
                    CurrentPg = "",
                    BranchName = "",
                    WorkItemIds = [],
                    ChildIds = [],
                    PrNumber = 0,
                    PrUrl = "",
                    CompletedPgs = completedPgs,
                    RemainingPgs = [],
                    TotalPgs = classified.Count,
                    AdoWorkspace = workspace,
                };
            }
            else
            {
                result = new BranchRouteResult
                {
                    Action = current.Action,
                    CurrentPg = current.Group.Name,
                    BranchName = current.Group.BranchName,
                    WorkItemIds = current.Group.WorkItemIds,
                    ChildIds = current.Group.ChildIds,
                    PrNumber = current.PrNumber,
                    PrUrl = current.PrUrl,
                    CompletedPgs = completedPgs,
                    RemainingPgs = remainingPgs,
                    TotalPgs = classified.Count,
                    AdoWorkspace = workspace,
                };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = EmptyRouteResult($"Error routing PG: {ex.Message}",
                await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
        }

        EmitRoute(result);
        return ExitCodes.Success;
    }

    private List<RoutePgGroup> BuildRouteGroups(
        int rootId,
        HierarchyResult root,
        IReadOnlyList<HierarchyResult> allItems,
        WorkspaceHint? hint)
    {
        var pgMap = new Dictionary<string, (List<int> Implementable, List<int> Container)>(StringComparer.Ordinal);
        foreach (var item in allItems)
        {
            var tag = ExtractPgTag(item.Tags);
            if (tag is null) continue;

            if (!pgMap.TryGetValue(tag, out var bucket))
            {
                bucket = (new List<int>(), new List<int>());
                pgMap[tag] = bucket;
            }

            var isImplementable = item.Facets.Contains("implementable");
            var isPlannable = item.Facets.Contains("plannable");
            var hasChildren = item.Children is { Length: > 0 };
            // Issue-as-task: plannable+implementable with no children → implementable.
            if (isImplementable && (!isPlannable || !hasChildren))
            {
                bucket.Implementable.Add(item.WorkItemId);
            }
            else
            {
                bucket.Container.Add(item.WorkItemId);
            }
        }

        var groups = new List<RoutePgGroup>();
        if (pgMap.Count == 0)
        {
            // Fallback: synthesize a single PG-1 from the whole tree.
            var taskIds = allItems
                .Where(i =>
                {
                    var imp = i.Facets.Contains("implementable");
                    var pln = i.Facets.Contains("plannable");
                    var hc = i.Children is { Length: > 0 };
                    return imp && (!pln || !hc);
                })
                .Select(i => i.WorkItemId).ToList();
            var issueIds = allItems
                .Where(i =>
                {
                    var pln = i.Facets.Contains("plannable");
                    var imp = i.Facets.Contains("implementable");
                    var hc = i.Children is { Length: > 0 };
                    return pln && (!imp || hc);
                })
                .Select(i => i.WorkItemId).ToList();
            groups.Add(new RoutePgGroup(
                Name: "PG-1",
                BranchName: ResolveFeatureBranch(hint, rootId, root.Title),
                ChildIds: taskIds,
                WorkItemIds: issueIds));
        }
        else
        {
            foreach (var name in pgMap.Keys.OrderBy(SortKeyForPg))
            {
                var bucket = pgMap[name];
                groups.Add(new RoutePgGroup(
                    Name: name,
                    BranchName: ResolvePgBranch(hint, rootId, name),
                    ChildIds: bucket.Implementable,
                    WorkItemIds: bucket.Container));
            }
        }

        return groups;
    }

    private static List<ClassifiedPg> ClassifyGroups(
        IReadOnlyList<RoutePgGroup> groups,
        IReadOnlyList<HierarchyResult> allItems,
        IReadOnlyList<string> remoteBranches,
        IReadOnlyList<PullRequestSummary> mergedPrs,
        IReadOnlyList<PullRequestSummary> openPrs)
    {
        var classified = new List<ClassifiedPg>(groups.Count);
        foreach (var pg in groups)
        {
            var mergedPr = mergedPrs.FirstOrDefault(p =>
                string.Equals(p.HeadRefName, pg.BranchName, StringComparison.Ordinal));
            var openPr = openPrs.FirstOrDefault(p =>
                string.Equals(p.HeadRefName, pg.BranchName, StringComparison.Ordinal));

            if (mergedPr is not null)
            {
                // Stale-branch defense: a merged PR with all containers still
                // in the proposed/initial category is most likely a leftover
                // from a prior failed run.
                var stale = pg.WorkItemIds.Count > 0
                    && !pg.WorkItemIds.Any(id =>
                    {
                        var item = allItems.FirstOrDefault(i => i.WorkItemId == id);
                        if (item is null) return false;
                        var category = StateCategoryResolver.Resolve(item.State, entries: null);
                        return category != StateCategory.Proposed;
                    });

                if (!stale)
                {
                    classified.Add(new ClassifiedPg(pg, "all_complete", true,
                        mergedPr.Number, mergedPr.Url ?? ""));
                    continue;
                }

                classified.Add(new ClassifiedPg(pg, "create_branch", false, 0, ""));
                continue;
            }

            if (openPr is not null)
            {
                classified.Add(new ClassifiedPg(pg, "submit_pr", false,
                    openPr.Number, openPr.Url ?? ""));
                continue;
            }

            // No merged or open PR — fall back to ADO-state-only completion.
            // Prefer issue states when present; else use task states.
            var allDone = pg.WorkItemIds.Count > 0
                ? pg.WorkItemIds.All(id => IsItemTerminal(id, allItems))
                : pg.ChildIds.Count > 0 && pg.ChildIds.All(id => IsItemTerminal(id, allItems));

            if (allDone)
            {
                classified.Add(new ClassifiedPg(pg, "all_complete", true, 0, ""));
            }
            else
            {
                classified.Add(new ClassifiedPg(pg, "create_branch", false, 0, ""));
            }
        }
        return classified;
    }

    private static bool IsItemTerminal(int id, IReadOnlyList<HierarchyResult> allItems)
    {
        var item = allItems.FirstOrDefault(i => i.WorkItemId == id);
        return item is not null && IsTerminalCategory(item.State);
    }

    private static string ResolvePgBranch(WorkspaceHint? hint, int rootId, string pgName)
    {
        if (hint is { PgBranch: { Length: > 0 } template })
        {
            var pgNum = ExtractPgNumber(pgName);
            return template
                .Replace("{n}", pgNum, StringComparison.OrdinalIgnoreCase)
                .Replace("{pg}", pgNum, StringComparison.OrdinalIgnoreCase);
        }
        var slug = Slugify(pgName);
        var b = $"feature/{rootId}-{slug}";
        return b.Length > 60 ? b[..60] : b;
    }

    private static string ResolveFeatureBranch(WorkspaceHint? hint, int rootId, string title)
    {
        if (hint is { FeatureBranch: { Length: > 0 } template })
        {
            return template;
        }
        var slug = Slugify(title);
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        var b = $"feature/{rootId}-{slug}";
        return b.Length > 60 ? b[..60] : b;
    }

    private async Task<IReadOnlyList<string>> TryListRemoteBranchesAsync(CancellationToken ct)
    {
        try { return await git.ListRemoteBranchesAsync(ct).ConfigureAwait(false); }
        catch { return []; }
    }

    private async Task<IReadOnlyList<PullRequestSummary>> TryListPrsAsync(
        string repoSlug, string state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoSlug)) return [];
        try
        {
            return await gh.ListPullRequestsAsync(
                repoSlug,
                new PrListFilters(State: state, Limit: 50),
                ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private async Task<string> TryListRemoteSlugAsync(CancellationToken ct)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return "";
            var match = GitHubSlugRegex.Match(url);
            return match.Success ? match.Groups[1].Value : "";
        }
        catch
        {
            return "";
        }
    }

    private static BranchRouteResult EmptyRouteResult(string error, string adoWorkspace) => new()
    {
        Action = "error",
        CurrentPg = "",
        BranchName = "",
        WorkItemIds = [],
        ChildIds = [],
        PrNumber = 0,
        PrUrl = "",
        CompletedPgs = [],
        RemainingPgs = [],
        TotalPgs = 0,
        AdoWorkspace = adoWorkspace,
        Error = error,
    };

    private static void EmitRoute(BranchRouteResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchRouteResult));

    private sealed record RoutePgGroup(
        string Name,
        string BranchName,
        IReadOnlyList<int> ChildIds,
        IReadOnlyList<int> WorkItemIds);

    private sealed record ClassifiedPg(
        RoutePgGroup Group,
        string Action,
        bool Completed,
        int PrNumber,
        string PrUrl);
}

