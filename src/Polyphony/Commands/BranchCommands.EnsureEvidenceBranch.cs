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
    /// Idempotently ensure an evidence branch exists locally and on the
    /// remote. The branch name is built from the Rev 4 grammar via
    /// <see cref="BranchNameBuilder.Evidence(RootId, WorkItemId)"/> when
    /// <paramref name="rootId"/> differs from <paramref name="workItemId"/>,
    /// or via <see cref="BranchNameBuilder.EvidenceOrphan(WorkItemId)"/>
    /// when they match (the work item is its own root).
    ///
    /// <para>The base branch defaults to <c>feature/{root_id}</c>; pass
    /// <paramref name="fromRef"/> to override (e.g. branch off a sibling MG
    /// branch for layered evidence). If the base does not exist on the
    /// remote, the verb fails with <see cref="ExitCodes.RoutingFailure"/>.</para>
    /// </summary>
    /// <param name="workItemId">ADO work item id the evidence is for.</param>
    /// <param name="rootId">Root (root) work-item id. Defaults to <paramref name="workItemId"/>; when equal, the orphan branch form <c>evidence/{item}</c> is used.</param>
    /// <param name="fromRef">Optional base branch override. When empty, defaults to <c>feature/{root_id}</c>.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-evidence-branch")]
    [JournaledAction(Action = "branch_ensure_evidence_branch")]
    [VerbResult(typeof(BranchEnsureEvidenceResult))]
    public async Task<int> EnsureEvidenceBranch(
        int workItemId = RequiredInput.MissingInt,
        int rootId = 0,
        string fromRef = "",
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch ensure-evidence-branch",
            ("--work-item-id", workItemId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate inputs up front so bad CLI args produce ConfigError. ─
        if (!WorkItemId.TryParse(workItemId, out var item))
        {
            EmitEvidenceError(workItemId, rootId, fromRef, $"workItemId must be positive (got {workItemId})");
            return ExitCodes.ConfigError;
        }

        // Default root to the work item itself (orphan evidence). A
        // negative explicit root is a config error so callers don't get a
        // silent collapse to orphan when they meant to pass a real root.
        if (rootId < 0)
        {
            EmitEvidenceError(workItemId, rootId, fromRef, $"rootId must be non-negative (got {rootId})");
            return ExitCodes.ConfigError;
        }

        var resolvedApexId = rootId == 0 ? workItemId : rootId;
        if (!RootId.TryParse(resolvedApexId, out var root))
        {
            EmitEvidenceError(workItemId, rootId, fromRef, $"rootId must be positive (got {resolvedApexId})");
            return ExitCodes.ConfigError;
        }

        // ── 2. Resolve branch name (orphan vs combined) and base ref. ────
        var orphan = resolvedApexId == workItemId;
        var branch = orphan
            ? BranchNameBuilder.EvidenceOrphan(item).Value
            : BranchNameBuilder.Evidence(root, item).Value;

        var baseBranch = string.IsNullOrEmpty(fromRef)
            ? BranchNameBuilder.Feature(root).Value
            : fromRef;

        BranchEnsureEvidenceBranchPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("branch_ensure_evidence_branch", branch, resolvedApexId, workItemId),
            async innerCt =>
            {
                try
                {
                    // ── 3. Inspect current state. ────────────────────────────────
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

                        // Base is irrelevant when the target already exists locally,
                        // but we still report whether it's on the remote so the
                        // workflow can distinguish "evidence exists, but the root
                        // feature has been deleted" from a fully wired state.
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
                        // Need to materialize from base. Confirm it exists on remote first.
                        baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, innerCt).ConfigureAwait(false);
                        if (!baseRemoteExisted)
                        {
                            payload = new BranchEnsureEvidenceBranchPayload
                            {
                                RootId = resolvedApexId,
                                WorkItemId = workItemId,
                                BranchName = branch,
                                BaseBranch = baseBranch,
                                ResultAction = "error",
                                Succeeded = false,
                                WasMutated = false,
                                WasCreated = false,
                                WasPushed = false,
                                BaseFetched = false,
                                Orphan = orphan,
                                FromRef = fromRef,
                                Error = $"base branch '{baseBranch}' does not exist on remote '{remote}'. " +
                                    (string.IsNullOrEmpty(fromRef)
                                        ? $"Run 'polyphony branch ensure-feature' for root {resolvedApexId} first, or pass --from-ref to base evidence on a different branch."
                                        : "Verify the --from-ref value points at a branch that exists on the remote."),
                            };
                            EmitEvidenceError(
                                workItemId,
                                rootId,
                                fromRef,
                                payload.Error,
                                branch: branch,
                                baseBranch: baseBranch,
                                orphan: orphan);
                            return ExitCodes.RoutingFailure;
                        }

                        // If the base isn't local, fetch and check it out so the
                        // create-from-base step has a known local start point.
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

                    var result = new BranchEnsureEvidenceResult
                    {
                        Branch = branch,
                        BaseBranch = baseBranch,
                        Action = action,
                        RemoteExisted = remoteExisted,
                        Pushed = pushed,
                        BaseRemoteExisted = baseRemoteExisted,
                        BaseFetched = baseFetched,
                        CreatedFrom = createdFrom,
                        RootId = resolvedApexId,
                        ItemId = workItemId,
                        Orphan = orphan,
                        FromRef = fromRef,
                    };
                    payload = new BranchEnsureEvidenceBranchPayload
                    {
                        RootId = resolvedApexId,
                        WorkItemId = workItemId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = action,
                        Succeeded = true,
                        WasMutated = wasMutated,
                        WasCreated = string.Equals(action, "created", StringComparison.Ordinal),
                        WasPushed = pushed,
                        BaseFetched = baseFetched,
                        Orphan = orphan,
                        FromRef = fromRef,
                        Sha = await TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
                    };
                    EmitEvidence(result);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    payload = new BranchEnsureEvidenceBranchPayload
                    {
                        RootId = resolvedApexId,
                        WorkItemId = workItemId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        WasCreated = false,
                        WasPushed = false,
                        BaseFetched = false,
                        Orphan = orphan,
                        FromRef = fromRef,
                        Error = ex.Message,
                    };
                    EmitEvidenceError(
                        workItemId,
                        rootId,
                        fromRef,
                        ex.Message,
                        branch: branch,
                        baseBranch: baseBranch,
                        orphan: orphan);
                    return ExitCodes.CacheError;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.BranchEnsureEvidenceBranchPayload),
            ct: ct).ConfigureAwait(false);
    }

    private static void EmitEvidence(BranchEnsureEvidenceResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult));

    private static void EmitEvidenceError(
        int workItemId,
        int rootId,
        string fromRef,
        string message,
        string branch = "",
        string baseBranch = "",
        bool orphan = false)
    {
        var resolvedApexId = rootId == 0 ? workItemId : rootId;
        var result = new BranchEnsureEvidenceResult
        {
            Branch = branch,
            BaseBranch = baseBranch,
            Action = "error",
            RemoteExisted = false,
            Pushed = false,
            BaseRemoteExisted = false,
            BaseFetched = false,
            RootId = resolvedApexId,
            ItemId = workItemId,
            Orphan = orphan,
            FromRef = fromRef,
            Error = message,
        };
        EmitEvidence(result);
    }
}
