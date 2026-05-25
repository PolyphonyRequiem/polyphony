using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
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
    [MutatesResource(ResourceKind.GitBranch)]
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

        var resolvedRootId = rootId == 0 ? workItemId : rootId;
        if (!RootId.TryParse(resolvedRootId, out var root))
        {
            EmitEvidenceError(workItemId, rootId, fromRef, $"rootId must be positive (got {resolvedRootId})");
            return ExitCodes.ConfigError;
        }

        // ── 2. Resolve branch name (orphan vs combined) and base ref. ────
        var orphan = resolvedRootId == workItemId;
        var branch = orphan
            ? BranchNameBuilder.EvidenceOrphan(item).Value
            : BranchNameBuilder.Evidence(root, item).Value;

        var baseBranch = string.IsNullOrEmpty(fromRef)
            ? BranchNameBuilder.Feature(root).Value
            : fromRef;

        BranchEnsureEvidenceBranchPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("branch_ensure_evidence_branch", branch, resolvedRootId, workItemId),
            async innerCt =>
            {
                try
                {
                    var outcome = await _branchEnsurer.EnsureAsync(
                        new BranchSpec(branch, baseBranch, remote),
                        innerCt).ConfigureAwait(false);

                    if (outcome.Status == BranchEnsureStatus.BaseMissingOnRemote)
                    {
                        payload = new BranchEnsureEvidenceBranchPayload
                        {
                            RootId = resolvedRootId,
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
                                    ? $"Run 'polyphony branch ensure-feature' for root {resolvedRootId} first, or pass --from-ref to base evidence on a different branch."
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

                    var result = new BranchEnsureEvidenceResult
                    {
                        Branch = branch,
                        BaseBranch = baseBranch,
                        Action = outcome.Action,
                        RemoteExisted = outcome.RemoteExisted,
                        Pushed = outcome.Pushed,
                        BaseRemoteExisted = outcome.BaseRemoteExisted,
                        BaseFetched = outcome.BaseFetched,
                        CreatedFrom = outcome.CreatedFrom,
                        RootId = resolvedRootId,
                        ItemId = workItemId,
                        Orphan = orphan,
                        FromRef = fromRef,
                    };
                    payload = new BranchEnsureEvidenceBranchPayload
                    {
                        RootId = resolvedRootId,
                        WorkItemId = workItemId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = outcome.Action,
                        Succeeded = true,
                        WasMutated = outcome.WasMutated,
                        WasCreated = string.Equals(outcome.Action, "created", StringComparison.Ordinal),
                        WasPushed = outcome.Pushed,
                        BaseFetched = outcome.BaseFetched,
                        Orphan = orphan,
                        FromRef = fromRef,
                        Sha = await _branchEnsurer.TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
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
                        RootId = resolvedRootId,
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
            effectsSelector: _ => SelectEnsureEvidenceEffects(payload),
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
        var resolvedRootId = rootId == 0 ? workItemId : rootId;
        var result = new BranchEnsureEvidenceResult
        {
            Branch = branch,
            BaseBranch = baseBranch,
            Action = "error",
            RemoteExisted = false,
            Pushed = false,
            BaseRemoteExisted = false,
            BaseFetched = false,
            RootId = resolvedRootId,
            ItemId = workItemId,
            Orphan = orphan,
            FromRef = fromRef,
            Error = message,
        };
        EmitEvidence(result);
    }
}
