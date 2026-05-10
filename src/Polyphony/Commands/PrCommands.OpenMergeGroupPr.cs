using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a merge-group branch
    /// into its parent. Head is <c>mg/{root_id}_{mg_path}</c>; base is the
    /// parent merge-group branch when nested, or the feature branch when
    /// top-level. Reuses an existing open PR for the same head/base pair
    /// instead of creating a duplicate.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path.</param>
    /// <param name="title">Optional PR title; deterministic fallback used when empty.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-mg-pr")]
    [VerbResult(typeof(PrOpenMergeGroupResult))]
    public async Task<int> OpenMergeGroupPr(
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-mg-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMgError(rootId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMgError(
                rootId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;

        try
        {
            // Validate both head and base exist on the remote — gh pr create
            // would otherwise fail late with a less actionable error.
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitMgError(rootId, mgPath, $"head branch '{headBranch}' does not exist on remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitMgError(rootId, mgPath, $"base branch '{baseBranch}' does not exist on remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitMgError(rootId, mgPath, "Could not resolve repo slug from origin remote", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var prTitle = string.IsNullOrWhiteSpace(title)
                ? $"merge group {path.Canonical} for root #{rootId}"
                : title;
            var prBody = string.IsNullOrWhiteSpace(body)
                ? BuildDefaultMgBody(rootId, path.Canonical, headBranch, baseBranch)
                : body;

            // Reuse an existing open PR for the same head/base pair.
            var existing = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var found = existing[0];
                EmitMergeGroup(new PrOpenMergeGroupResult
                {
                    PrNumber = found.Number,
                    PrUrl = found.Url ?? "",
                    Title = prTitle,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    RootId = rootId,
                    MgPath = path.Canonical,
                    Created = false,
                });
                return ExitCodes.Success;
            }

            var url = await gh.CreatePullRequestAsync(slug, baseBranch, headBranch, prTitle, prBody, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                EmitMgError(rootId, mgPath, "gh pr create failed — no URL returned", headBranch: headBranch, baseBranch: baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var trimmedUrl = url.Trim();
            EmitMergeGroup(new PrOpenMergeGroupResult
            {
                PrNumber = ExtractPrNumber(trimmedUrl),
                PrUrl = trimmedUrl,
                Title = prTitle,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                RootId = rootId,
                MgPath = path.Canonical,
                Created = true,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitMgError(rootId, mgPath, ex.Message, headBranch: headBranch, baseBranch: baseBranch);
            return ExitCodes.RoutingFailure;
        }
    }

    private static string BuildDefaultMgBody(int rootId, string mgPath, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Merge group `").Append(mgPath).Append("` for root #").Append(rootId).Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("This PR was opened by `polyphony pr open-mg-pr`. The detailed body — including the manifest of items in this merge group — is composed by the orchestrating workflow when it has that context.\n");
        return sb.ToString();
    }

    private static void EmitMergeGroup(PrOpenMergeGroupResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenMergeGroupResult));

    private static void EmitMgError(
        int rootId,
        string mgPath,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitMergeGroup(new PrOpenMergeGroupResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            MgPath = mgPath,
            Created = false,
            Error = message,
        });
    }
}
