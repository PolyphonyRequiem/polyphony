using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Idempotently ensure a plan branch exists locally and on the
    /// remote. The verb supports the three plan-branch shapes from the
    /// Rev 4 ADR (root plan, child of root plan, deeper descendant) and
    /// derives the base branch from the input args:
    /// <list type="bullet">
    ///   <item><description>Root plan (<c>--item-id == --root-id</c>): branch <c>plan/{root}</c>, base <c>feature/{root}</c>.</description></item>
    ///   <item><description>Child of root plan (no <c>--parent-item-id</c>): branch <c>plan/{root}-{item_id}</c>, base <c>plan/{root}</c>.</description></item>
    ///   <item><description>Descendant plan (<c>--parent-item-id</c> provided): branch <c>plan/{root}-{item_id}</c>, base <c>plan/{root}-{parent_item_id}</c>. Plan branches are FLAT — hierarchy is captured by the PR base branch, not the name.</description></item>
    /// </list>
    /// Materializes the base from the remote first if it exists only there.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the item this plan belongs to. Equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="parentItemId">Immediate plan-tree parent's work-item id. Required for descendants of descendants; omit for root plan and direct children of root plan.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-plan")]
    [VerbResult(typeof(BranchEnsurePlanResult))]
    public async Task<int> EnsurePlan(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int parentItemId = 0,
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch ensure-plan",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate inputs up front so bad CLI args produce ConfigError,
        //      not a misleading CacheError later. ──────────────────────────
        if (!RootId.TryParse(rootId, out var root))
        {
            EmitPlanError(rootId, itemId, parentItemId, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitPlanError(rootId, itemId, parentItemId, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        bool isRootPlan = itemId == rootId;
        int? parent;
        string baseBranch;
        string branch;

        if (isRootPlan)
        {
            // Root plan: parent-item-id MUST be absent (default 0). Reject
            // explicit overrides so input semantics stay unambiguous.
            if (parentItemId != 0)
            {
                EmitPlanError(rootId, itemId, parentItemId,
                    $"--parent-item-id must not be provided when --item-id == --root-id (got {parentItemId}); the root plan has no parent.");
                return ExitCodes.ConfigError;
            }

            parent = null;
            branch = BranchNameBuilder.RootPlan(root).Value;
            baseBranch = BranchNameBuilder.Feature(root).Value;
        }
        else
        {
            // Descendant plan: validate the optional parent-item-id.
            if (parentItemId == 0)
            {
                // Child of root plan — implicit parent is the root plan branch.
                parent = null;
                branch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.RootPlan(root).Value;
            }
            else
            {
                if (!WorkItemId.TryParse(parentItemId, out var parentItem))
                {
                    EmitPlanError(rootId, itemId, parentItemId, $"--parent-item-id must be positive (got {parentItemId})");
                    return ExitCodes.ConfigError;
                }

                if (parentItemId == itemId)
                {
                    EmitPlanError(rootId, itemId, parentItemId,
                        $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");
                    return ExitCodes.ConfigError;
                }

                if (parentItemId == rootId)
                {
                    EmitPlanError(rootId, itemId, parentItemId,
                        $"--parent-item-id ({parentItemId}) equals --root-id; omit --parent-item-id when the parent is the root plan.");
                    return ExitCodes.ConfigError;
                }

                parent = parentItemId;
                branch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
            }
        }

        try
        {
            // ── 2. Check current state of plan branch on remote and locally. ─
            var remoteRefs = await git.LsRemoteHeadsAsync(remote, branch, ct).ConfigureAwait(false);
            var remoteExisted = remoteRefs.Count > 0;

            var localSha = await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
            var localExisted = localSha is not null;

            string action;
            bool pushed = false;
            string? createdFrom = null;
            bool baseRemoteExisted;
            bool baseFetched = false;

            if (localExisted)
            {
                await git.CheckoutAsync(branch, ct).ConfigureAwait(false);
                action = "checked_out";

                if (!remoteExisted)
                {
                    await git.PushAsync(branch, remote, ct).ConfigureAwait(false);
                    pushed = true;
                }

                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
            }
            else if (remoteExisted)
            {
                await git.FetchAsync(remote, branch, ct).ConfigureAwait(false);
                await git.CheckoutTrackingAsync(branch, remote, ct).ConfigureAwait(false);
                action = "checked_out";
                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
            }
            else
            {
                // ── 3. Need to materialize from base. Confirm base exists on remote. ─
                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
                if (!baseRemoteExisted)
                {
                    var hint = isRootPlan
                        ? "Run 'polyphony branch ensure-feature' first to create the feature branch."
                        : (parent is null
                            ? "Run 'polyphony branch ensure-plan' for the root plan first (--item-id == --root-id)."
                            : $"Run 'polyphony branch ensure-plan --root-id {rootId} --item-id {parentItemId}' first to create the parent plan branch.");

                    EmitPlanError(rootId, itemId, parentItemId,
                        $"base branch '{baseBranch}' does not exist on remote '{remote}'. {hint}",
                        branch: branch,
                        baseBranch: baseBranch,
                        isRootPlan: isRootPlan);
                    return ExitCodes.RoutingFailure;
                }

                // If the base isn't local, fetch and check it out so the
                // create-from-base step has a known local start point.
                var baseLocalSha = await git.RevParseLocalBranchAsync(baseBranch, ct).ConfigureAwait(false);
                if (baseLocalSha is null)
                {
                    await git.FetchAsync(remote, baseBranch, ct).ConfigureAwait(false);
                    await git.CheckoutTrackingAsync(baseBranch, remote, ct).ConfigureAwait(false);
                    baseFetched = true;
                }

                await git.CreateBranchAsync(branch, baseBranch, ct).ConfigureAwait(false);
                await git.PushAsync(branch, remote, ct).ConfigureAwait(false);
                action = "created";
                pushed = true;
                createdFrom = baseBranch;
            }

            var result = new BranchEnsurePlanResult
            {
                Branch = branch,
                BaseBranch = baseBranch,
                Action = action,
                RemoteExisted = remoteExisted,
                Pushed = pushed,
                BaseRemoteExisted = baseRemoteExisted,
                BaseFetched = baseFetched,
                CreatedFrom = createdFrom,
                RootId = rootId,
                ItemId = itemId,
                ParentItemId = parent,
                IsRootPlan = isRootPlan,
            };
            EmitPlan(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitPlanError(rootId, itemId, parentItemId, ex.Message,
                branch: branch,
                baseBranch: baseBranch,
                isRootPlan: isRootPlan);
            return ExitCodes.CacheError;
        }
    }

    private static void EmitPlan(BranchEnsurePlanResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchEnsurePlanResult));

    private static void EmitPlanError(
        int rootId,
        int itemId,
        int parentItemId,
        string message,
        string branch = "",
        string baseBranch = "",
        bool isRootPlan = false)
    {
        var result = new BranchEnsurePlanResult
        {
            Branch = branch,
            BaseBranch = baseBranch,
            Action = "error",
            RemoteExisted = false,
            Pushed = false,
            BaseRemoteExisted = false,
            BaseFetched = false,
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId == 0 ? null : parentItemId,
            IsRootPlan = isRootPlan,
            Error = message,
        };
        EmitPlan(result);
    }
}
