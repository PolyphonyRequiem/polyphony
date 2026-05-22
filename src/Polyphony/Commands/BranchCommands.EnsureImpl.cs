using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

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
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-impl")]
    [JournaledAction(Action = "branch_ensure_impl")]
    [VerbResult(typeof(BranchEnsureImplResult))]
    public async Task<int> EnsureImpl(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch ensure-impl",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

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
        BranchEnsureImplPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("branch_ensure_impl", branch, rootId, itemId),
            async innerCt =>
            {
                try
                {
                    var remoteRefs = await git.LsRemoteHeadsAsync(remote, branch, innerCt).ConfigureAwait(false);
                    var remoteExisted = remoteRefs.Count > 0;

                    var localSha = await git.RevParseLocalBranchAsync(branch, innerCt).ConfigureAwait(false);
                    var localExisted = localSha is not null;
                    var currentBranch = localExisted
                        ? await TryGetCurrentBranchAsync(innerCt).ConfigureAwait(false)
                        : null;

                    string action;
                    bool pushed = false;
                    string? createdFrom = null;
                    bool baseRemoteExisted;
                    bool baseFetched = false;
                    bool wasMutated;

                    if (localExisted)
                    {
                        await git.CheckoutAsync(branch, innerCt).ConfigureAwait(false);
                        action = "checked_out";
                        wasMutated = currentBranch is null || !string.Equals(currentBranch, branch, StringComparison.Ordinal);

                        if (!remoteExisted)
                        {
                            await git.PushAsync(branch, remote, innerCt).ConfigureAwait(false);
                            pushed = true;
                            wasMutated = true;
                        }

                        baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, innerCt).ConfigureAwait(false);
                    }
                    else if (remoteExisted)
                    {
                        await git.FetchAsync(remote, branch, innerCt).ConfigureAwait(false);
                        await git.CheckoutTrackingAsync(branch, remote, innerCt).ConfigureAwait(false);
                        action = "checked_out";
                        baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, innerCt).ConfigureAwait(false);
                        wasMutated = true;
                    }
                    else
                    {
                        baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, innerCt).ConfigureAwait(false);
                        if (!baseRemoteExisted)
                        {
                            payload = new BranchEnsureImplPayload
                            {
                                RootId = rootId,
                                WorkItemId = itemId,
                                MergeGroupPath = path.Canonical,
                                BranchName = branch,
                                BaseBranch = baseBranch,
                                ResultAction = "error",
                                Succeeded = false,
                                WasMutated = false,
                                WasCreated = false,
                                WasPushed = false,
                                BaseFetched = false,
                                Error = $"base merge-group branch '{baseBranch}' does not exist on remote '{remote}'. Run 'polyphony branch ensure-mg' for this path first.",
                            };
                            EmitImplError(
                                rootId,
                                itemId,
                                mgPath,
                                payload.Error,
                                branch: branch,
                                baseBranch: baseBranch);
                            return ExitCodes.RoutingFailure;
                        }

                        var baseLocalSha = await git.RevParseLocalBranchAsync(baseBranch, innerCt).ConfigureAwait(false);
                        if (baseLocalSha is null)
                        {
                            await git.FetchAsync(remote, baseBranch, innerCt).ConfigureAwait(false);
                            await git.CheckoutTrackingAsync(baseBranch, remote, innerCt).ConfigureAwait(false);
                            baseFetched = true;
                        }

                        await git.CreateBranchAsync(branch, baseBranch, innerCt).ConfigureAwait(false);
                        await git.PushAsync(branch, remote, innerCt).ConfigureAwait(false);
                        action = "created";
                        pushed = true;
                        createdFrom = baseBranch;
                        wasMutated = true;
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
                    payload = new BranchEnsureImplPayload
                    {
                        RootId = rootId,
                        WorkItemId = itemId,
                        MergeGroupPath = path.Canonical,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = action,
                        Succeeded = true,
                        WasMutated = wasMutated,
                        WasCreated = string.Equals(action, "created", StringComparison.Ordinal),
                        WasPushed = pushed,
                        BaseFetched = baseFetched,
                        Sha = await TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
                    };
                    EmitImpl(result);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    payload = new BranchEnsureImplPayload
                    {
                        RootId = rootId,
                        WorkItemId = itemId,
                        MergeGroupPath = path.Canonical,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        WasCreated = false,
                        WasPushed = false,
                        BaseFetched = false,
                        Error = ex.Message,
                    };
                    EmitImplError(rootId, itemId, mgPath, ex.Message, branch: branch, baseBranch: baseBranch);
                    return ExitCodes.CacheError;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.BranchEnsureImplPayload),
            ct: ct).ConfigureAwait(false);
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
