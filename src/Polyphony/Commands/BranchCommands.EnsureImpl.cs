using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Idempotently ensure a impl branch exists locally and on the remote.
    /// The branch name is built from the Rev 4 grammar via
    /// <see cref="BranchNameBuilder.Impl(RootId, WorkItemId)"/>; the base
    /// branch is the enclosing merge-group branch
    /// (<c>mg/{root_id}_{mg_path}</c>). Materializes the base from the
    /// remote first if it exists only there.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-impl")]
    [VerbResult(typeof(BranchEnsureImplResult))]
    public async Task<int> EnsureImpl(
        int rootId,
        int itemId,
        string mgPath,
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (!RootId.TryParse(rootId, out var root))
        {
            EmitImplError(rootId, itemId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitImplError(rootId, itemId, mgPath, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitImplError(
                rootId,
                itemId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        if (path.ExceedsDefaultHardStopDepth)
        {
            EmitImplError(
                rootId,
                itemId,
                mgPath,
                $"merge-group path depth {path.Depth} exceeds the hard-stop limit ({MergeGroupPath.DefaultHardStopDepth}).");
            return ExitCodes.ConfigError;
        }

        var branch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;

        try
        {
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
                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
                if (!baseRemoteExisted)
                {
                    EmitImplError(
                        rootId,
                        itemId,
                        mgPath,
                        $"base merge-group branch '{baseBranch}' does not exist on remote '{remote}'. " +
                        "Run 'polyphony branch ensure-mg' for this path first.",
                        branch: branch,
                        baseBranch: baseBranch);
                    return ExitCodes.RoutingFailure;
                }

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

            var result = new BranchEnsureImplResult
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
                MgPath = path.Canonical,
            };
            EmitImpl(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitImplError(rootId, itemId, mgPath, ex.Message, branch: branch, baseBranch: baseBranch);
            return ExitCodes.CacheError;
        }
    }

    private static void EmitImpl(BranchEnsureImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchEnsureImplResult));

    private static void EmitImplError(
        int rootId,
        int itemId,
        string mgPath,
        string message,
        string branch = "",
        string baseBranch = "")
    {
        var result = new BranchEnsureImplResult
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
            MgPath = mgPath,
            Error = message,
        };
        EmitImpl(result);
    }
}
