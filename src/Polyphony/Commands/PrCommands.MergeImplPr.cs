using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Merge the per-item impl PR into its enclosing merge-group branch.
    /// Identifies the PR by its (head, base) pair: head is
    /// <c>impl/{root_id}-{item_id}</c>, base is <c>mg/{root_id}_{mg_path}</c>.
    /// Default merge method is squash because impl PRs carry micro-history
    /// we do not want to pollute the merge-group branch with; the planner
    /// may override per item via <paramref name="method"/>.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="method">
    /// Merge method: <c>squash</c> (default), <c>merge</c>, or <c>rebase</c>.
    /// </param>
    /// <param name="admin">Pass <c>--admin</c> to bypass branch-protection requirements.</param>
    /// <param name="deleteBranch">Delete the head branch after merge. Default true.</param>
    /// <param name="matchHeadCommit">
    /// When set, gh refuses to merge if the head SHA has moved off this
    /// commit. Use to guard against races between status checks and merge.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-impl-pr")]
    public async Task<int> MergeImplPr(
        int rootId,
        int itemId,
        string mgPath,
        string method = "squash",
        bool admin = false,
        bool deleteBranch = true,
        string matchHeadCommit = "",
        CancellationToken ct = default)
    {
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMergeImplError(
                rootId,
                itemId,
                mgPath,
                method,
                deleteBranch,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        if (!TryParseMethod(method, out var mergeMethod, out var methodError))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, methodError);
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;

        try
        {
            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, "Could not resolve repo slug from origin remote", headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var resolution = await FindPrForMergeAsync(slug, headBranch, baseBranch, ct).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, resolution.Error, headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            // Idempotent: if the matching PR is already merged, treat as success.
            if (resolution.AlreadyMergedPr is { } already)
            {
                EmitMergeImpl(new PrMergeImplResult
                {
                    PrNumber = already.Number,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    RootId = rootId,
                    ItemId = itemId,
                    MgPath = path.Canonical,
                    Method = method,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = deleteBranch,
                    MergeSha = null,
                });
                return ExitCodes.Success;
            }

            var openPr = resolution.OpenPr!;
            var mergeMatch = string.IsNullOrEmpty(matchHeadCommit) ? null : matchHeadCommit;
            var result = await gh.MergePullRequestAsync(
                slug, openPr.Number, mergeMethod, admin, deleteBranch, mergeMatch, ct).ConfigureAwait(false);

            EmitMergeImpl(new PrMergeImplResult
            {
                PrNumber = openPr.Number,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                RootId = rootId,
                ItemId = itemId,
                MgPath = path.Canonical,
                Method = method,
                Merged = result.Succeeded,
                AlreadyMerged = result.AlreadyMerged,
                DeleteBranch = deleteBranch,
                MergeSha = result.MergeSha,
            });
            return result.Succeeded ? ExitCodes.Success : ExitCodes.RoutingFailure;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranch, ex.Message, headBranch, baseBranch);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitMergeImpl(PrMergeImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeImplResult));

    private static void EmitMergeImplError(
        int rootId,
        int itemId,
        string mgPath,
        string method,
        bool deleteBranch,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitMergeImpl(new PrMergeImplResult
        {
            PrNumber = 0,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath,
            Method = method,
            Merged = false,
            AlreadyMerged = false,
            DeleteBranch = deleteBranch,
            MergeSha = null,
            Error = message,
        });
    }
}
