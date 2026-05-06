using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a task branch into
    /// its enclosing merge-group branch. Head is <c>task/{root_id}-{item_id}</c>;
    /// base is <c>mg/{root_id}_{mg_path}</c>. Reuses an existing open PR
    /// for the same head/base pair instead of creating a duplicate.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the cached work-item title.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-task-pr")]
    public async Task<int> OpenTaskPr(
        int rootId,
        int itemId,
        string mgPath,
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitTaskError(rootId, itemId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitTaskError(rootId, itemId, mgPath, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitTaskError(
                rootId,
                itemId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.Task(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;

        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitTaskError(rootId, itemId, mgPath, $"head branch '{headBranch}' does not exist on remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitTaskError(rootId, itemId, mgPath, $"base branch '{baseBranch}' does not exist on remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitTaskError(rootId, itemId, mgPath, "Could not resolve repo slug from origin remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var prTitle = string.IsNullOrWhiteSpace(title)
                ? await ResolveTaskPrTitleAsync(itemId, ct).ConfigureAwait(false)
                : title;
            var prBody = string.IsNullOrWhiteSpace(body)
                ? BuildDefaultTaskBody(rootId, itemId, path.Canonical, headBranch, baseBranch)
                : body;

            var existing = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var found = existing[0];
                EmitTask(new PrOpenTaskResult
                {
                    PrNumber = found.Number,
                    PrUrl = found.Url ?? "",
                    Title = prTitle,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    RootId = rootId,
                    ItemId = itemId,
                    MgPath = path.Canonical,
                    Created = false,
                });
                return ExitCodes.Success;
            }

            var url = await gh.CreatePullRequestAsync(slug, baseBranch, headBranch, prTitle, prBody, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                EmitTaskError(rootId, itemId, mgPath, "gh pr create failed — no URL returned", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var trimmedUrl = url.Trim();
            EmitTask(new PrOpenTaskResult
            {
                PrNumber = ExtractPrNumber(trimmedUrl),
                PrUrl = trimmedUrl,
                Title = prTitle,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                RootId = rootId,
                ItemId = itemId,
                MgPath = path.Canonical,
                Created = true,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitTaskError(rootId, itemId, mgPath, ex.Message, headBranch: headBranch, baseBranch: baseBranch);
            return ExitCodes.RoutingFailure;
        }
    }

    private async Task<string> ResolveTaskPrTitleAsync(int itemId, CancellationToken ct)
    {
        var fallback = $"task #{itemId}";
        try
        {
            var tree = await twig.ShowTreeAsync(itemId, ct).ConfigureAwait(false);
            var workItemTitle = tree?["title"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(workItemTitle) ? fallback : $"{workItemTitle} AB#{itemId}";
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildDefaultTaskBody(int rootId, int itemId, string mgPath, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Task #").Append(itemId).Append(" for root #").Append(rootId).Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into merge group `").Append(mgPath)
          .Append("` (base `").Append(baseBranch).Append("`).\n\n");
        sb.Append("AB#").Append(itemId).Append('\n');
        return sb.ToString();
    }

    private static void EmitTask(PrOpenTaskResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenTaskResult));

    private static void EmitTaskError(
        int rootId,
        int itemId,
        string mgPath,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitTask(new PrOpenTaskResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath,
            Created = false,
            Error = message,
        });
    }
}
