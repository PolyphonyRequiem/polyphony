using System.Text.Json;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony branch load-tree</c>: discover the work-item hierarchy
/// rooted at the given epic, group children into PR groups via PG-N tags,
/// match each PG to its merged PR (if any), and emit completion +
/// reconciliation summaries. Migrated from <c>scripts/load-work-tree.ps1</c>.
/// </summary>
public sealed partial class BranchCommands
{
    private static readonly Regex GitHubSlugRegex =
        new(@"github\.com(?:/|:)([^/]+/[^/.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Loads the work-item tree rooted at <paramref name="workItem"/>,
    /// discovers PR groups, and reports completion + reconciliation status.
    /// </summary>
    /// <param name="workItem">ADO work item ID — root of the hierarchy (typically an epic).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("load-tree")]
    public async Task<int> LoadTree(int workItem, CancellationToken ct = default)
    {
        BranchLoadTreeResult result;
        try
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);

            var hierarchy = await walker.WalkAsync(workItem, maxDepth: 3, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                result = EmptyLoadTreeResult(workItem, $"Work item {workItem} not found",
                    await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
                EmitLoadTree(result);
                return ExitCodes.Success;
            }

            var allItems = Flatten(hierarchy).ToList();
            var workTree = BuildWorkTree(hierarchy);

            // Resolve repo + merged PRs in parallel with no-throw fallbacks
            // (failures here degrade gracefully — completion just stays false).
            var repoSlug = await TryResolveRepoSlugAsync(ct).ConfigureAwait(false);
            var mergedPrs = await TryListMergedPrsAsync(repoSlug, ct).ConfigureAwait(false);

            var prGroups = BuildPrGroups(hierarchy, allItems, mergedPrs);

            var completed = prGroups.Where(p => p.Completed).Select(p => p.Name).ToList();
            var pending = prGroups.Where(p => !p.Completed).Select(p => p.Name).ToList();
            var nextPg = pending.Count > 0 ? pending[0] : "";
            var reconcile = prGroups
                .Where(p => p.NeedsReconciliation)
                .Select(p => new PgReconciliation
                {
                    Name = p.Name,
                    NonDoneTaskIds = p.NonDoneTaskIds,
                    StaleDoingTaskIds = p.StaleDoingTaskIds,
                    NonDoneIssueIds = p.NonDoneIssueIds,
                })
                .ToList();

            var taggedCount = allItems.Count(i => ExtractPgTag(i.Tags) is not null);
            var totalTasks = allItems.Count(i =>
                i.Capabilities.Contains("implementable") && !i.Capabilities.Contains("plannable"));
            var totalIssues = allItems.Count(i => i.Capabilities.Contains("plannable"));

            var org = await twig.GetConfigValueAsync("organization", ct).ConfigureAwait(false) ?? "";
            var proj = await twig.GetConfigValueAsync("project", ct).ConfigureAwait(false) ?? "";
            var workspace = !string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(proj) ? $"{org}/{proj}" : "";

            result = new BranchLoadTreeResult
            {
                WorkTree = workTree,
                PrGroups = prGroups,
                CompletedPgs = completed,
                PendingPgs = pending,
                NextPg = nextPg,
                PgsNeedingReconciliation = reconcile,
                TotalTasks = totalTasks,
                TotalIssues = totalIssues,
                TaggedItems = taggedCount,
                UntaggedItems = allItems.Count - taggedCount,
                AdoOrg = org,
                AdoProject = proj,
                AdoWorkspace = workspace,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = EmptyLoadTreeResult(workItem, $"Error loading work tree: {ex.Message}",
                await TryResolveAdoWorkspaceAsync(ct).ConfigureAwait(false));
        }

        EmitLoadTree(result);
        return ExitCodes.Success;
    }

    private static WorkTree BuildWorkTree(HierarchyResult root)
    {
        var issues = new List<WorkTreeIssue>();
        var children = root.Children ?? [];
        foreach (var child in children)
        {
            var taskNodes = child.Children ?? [];
            var tasks = taskNodes.Select(t => new WorkTreeTask
            {
                Id = t.WorkItemId,
                Title = t.Title,
                State = t.State,
                Tags = t.Tags ?? "",
            }).ToList();

            issues.Add(new WorkTreeIssue
            {
                Id = child.WorkItemId,
                Title = child.Title,
                State = child.State,
                Type = child.Type,
                Tags = child.Tags ?? "",
                TaskCount = tasks.Count,
                Tasks = tasks,
            });
        }

        return new WorkTree
        {
            EpicId = root.WorkItemId,
            EpicTitle = root.Title,
            EpicType = root.Type,
            Issues = issues,
        };
    }

    private static IReadOnlyList<PullRequestGroup> BuildPrGroups(
        HierarchyResult root,
        IReadOnlyList<HierarchyResult> allItems,
        IReadOnlyList<PullRequestSummary> mergedPrs)
    {
        // Group items by their PG-N tag — replicates Group-ByPG semantics
        // from scripts/lib/pg-helpers.ps1.
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

            var isImplementable = item.Capabilities.Contains("implementable");
            var isContainer = item.Capabilities.Contains("plannable");
            var hasChildren = item.Children is { Length: > 0 };
            // Issue-as-task: plannable+implementable with no children → implementable.
            if (isImplementable && (!isContainer || !hasChildren))
            {
                bucket.Implementable.Add(item.WorkItemId);
            }
            else
            {
                bucket.Container.Add(item.WorkItemId);
            }
        }

        var groups = new List<PullRequestGroup>();
        if (pgMap.Count == 0)
        {
            // Fallback: no PG tags found → synthesize a single PG-1.
            var slug = Slugify(root.Title);
            var taskIds = allItems
                .Where(i => i.Capabilities.Contains("implementable") && !i.Capabilities.Contains("plannable"))
                .Select(i => i.WorkItemId).ToList();
            var issueIds = allItems
                .Where(i => i.Capabilities.Contains("plannable"))
                .Select(i => i.WorkItemId).ToList();
            groups.Add(BuildOnePg("PG-1", taskIds, issueIds, NewBranchName($"pg-1-{slug}"),
                allItems, mergedPrs, isFallback: true));
        }
        else
        {
            foreach (var name in pgMap.Keys.OrderBy(SortKeyForPg))
            {
                var bucket = pgMap[name];
                var branch = NewBranchName(Slugify(name));
                groups.Add(BuildOnePg(name, bucket.Implementable, bucket.Container, branch,
                    allItems, mergedPrs, isFallback: false));
            }
        }

        return groups;
    }

    private static PullRequestGroup BuildOnePg(
        string name,
        IReadOnlyList<int> taskIds,
        IReadOnlyList<int> issueIds,
        string branchName,
        IReadOnlyList<HierarchyResult> allItems,
        IReadOnlyList<PullRequestSummary> mergedPrs,
        bool isFallback)
    {
        var match = mergedPrs.FirstOrDefault(p => string.Equals(p.HeadRefName, branchName, StringComparison.Ordinal));
        var mergedPr = match?.Number ?? 0;

        var allIds = taskIds.Concat(issueIds).ToHashSet();
        var pgItems = allItems.Where(i => allIds.Contains(i.WorkItemId)).ToList();
        var allDone = pgItems.Count > 0 && pgItems.All(i => IsTerminalCategory(i.State));

        // Fallback PG only counts as "completed" once both the PR is merged
        // AND every item is terminal — preserves the load-work-tree.ps1
        // semantic where ungrouped scopes need both signals to close.
        var completed = isFallback ? mergedPr > 0 && allDone : mergedPr > 0;

        IReadOnlyList<int> nonDoneTasks = [];
        IReadOnlyList<int> staleDoingTasks = [];
        IReadOnlyList<int> nonDoneIssues = [];
        if (completed)
        {
            var taskSet = taskIds.ToHashSet();
            var issueSet = issueIds.ToHashSet();
            nonDoneTasks = allItems
                .Where(i => taskSet.Contains(i.WorkItemId) && !IsTerminalCategory(i.State))
                .Select(i => i.WorkItemId).ToList();
            staleDoingTasks = allItems
                .Where(i => taskSet.Contains(i.WorkItemId)
                    && string.Equals(i.State, "Doing", StringComparison.Ordinal))
                .Select(i => i.WorkItemId).ToList();
            nonDoneIssues = allItems
                .Where(i => issueSet.Contains(i.WorkItemId) && !IsTerminalCategory(i.State))
                .Select(i => i.WorkItemId).ToList();
        }

        return new PullRequestGroup
        {
            Name = name,
            TaskIds = taskIds,
            IssueIds = issueIds,
            BranchNameSuggestion = branchName,
            MergedPr = mergedPr,
            Completed = completed,
            NonDoneTaskIds = nonDoneTasks,
            StaleDoingTaskIds = staleDoingTasks,
            NonDoneIssueIds = nonDoneIssues,
            NeedsReconciliation = staleDoingTasks.Count > 0 || nonDoneIssues.Count > 0,
        };
    }

    private static int SortKeyForPg(string pg)
    {
        // "PG-3" → 3; non-conforming names sort last.
        if (pg.StartsWith("PG-", StringComparison.Ordinal)
            && int.TryParse(pg[3..], out var n))
        {
            return n;
        }
        return int.MaxValue;
    }

    private static string Slugify(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sanitized = SlugRegex().Replace(input, "-").ToLowerInvariant();
        return sanitized.Trim('-');
    }

    private static string NewBranchName(string slug)
    {
        var b = $"feature/{slug}";
        return b.Length > 60 ? b[..60] : b;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex SlugRegex();

    private async Task<string> TryResolveRepoSlugAsync(CancellationToken ct)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return "";
            var match = GitHubSlugRegex.Match(url);
            return match.Success ? match.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private async Task<IReadOnlyList<PullRequestSummary>> TryListMergedPrsAsync(
        string repoSlug, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoSlug)) return [];
        try
        {
            return await gh.ListPullRequestsAsync(
                repoSlug,
                new PrListFilters(State: "merged", Limit: 50),
                ct).ConfigureAwait(false);
        }
        catch { return []; }
    }

    private static BranchLoadTreeResult EmptyLoadTreeResult(int workItem, string error, string adoWorkspace) => new()
    {
        WorkTree = new WorkTree { EpicId = workItem, EpicTitle = "", EpicType = "", Issues = [] },
        PrGroups = [],
        CompletedPgs = [],
        PendingPgs = [],
        NextPg = "",
        PgsNeedingReconciliation = [],
        TotalTasks = 0,
        TotalIssues = 0,
        TaggedItems = 0,
        UntaggedItems = 0,
        AdoOrg = "",
        AdoProject = "",
        AdoWorkspace = adoWorkspace,
        Error = error,
    };

    private static void EmitLoadTree(BranchLoadTreeResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.BranchLoadTreeResult));
}
