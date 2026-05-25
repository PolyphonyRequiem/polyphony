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
    /// Idempotently ensure a merge-group branch exists locally and on the
    /// remote. The branch name is built from the Rev 4 grammar via
    /// <see cref="BranchNameBuilder.MergeGroup(RootId, MergeGroupPath)"/>; the
    /// base branch is auto-derived from the path (top-level → feature
    /// branch; nested → parent merge-group branch). Materializes the base
    /// from the remote first if it exists only there.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path (each segment matches <c>^[a-z][a-z0-9-]{0,30}$</c>).</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-mg")]
    [JournaledAction(Action = "branch_ensure_merge_group")]
    [MutatesResource(ResourceKind.GitBranch)]
    [VerbResult(typeof(BranchEnsureMergeGroupResult))]
    public async Task<int> EnsureMergeGroup(
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch ensure-mg",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        // ── 1. Validate inputs up front so bad CLI args produce ConfigError,
        //      not a misleading CacheError later. ──────────────────────────
        if (!RootId.TryParse(rootId, out var root))
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

        if (path.ExceedsDefaultHardStopDepth)
        {
            EmitMgError(
                rootId,
                mgPath,
                $"merge-group path depth {path.Depth} exceeds the hard-stop limit ({MergeGroupPath.DefaultHardStopDepth}). The default-nest trigger should not produce this depth without explicit operator approval.",
                depthExceeded: true,
                depth: path.Depth);
            return ExitCodes.ConfigError;
        }

        var branch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;
        BranchEnsureMergeGroupPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("branch_ensure_merge_group", branch, rootId, rootId),
            async innerCt =>
            {
                try
                {
                    var outcome = await _branchEnsurer.EnsureAsync(
                        new BranchSpec(branch, baseBranch, remote),
                        innerCt).ConfigureAwait(false);

                    if (outcome.Status == BranchEnsureStatus.BaseMissingOnRemote)
                    {
                        payload = new BranchEnsureMergeGroupPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            Depth = path.Depth,
                            BranchName = branch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            WasCreated = false,
                            WasPushed = false,
                            BaseFetched = false,
                            Error = $"base branch '{baseBranch}' does not exist on remote '{remote}'. " +
                                (path.IsTopLevel
                                    ? "Run 'polyphony branch ensure-feature' first to create the feature branch."
                                    : "Run 'polyphony branch ensure-mg' for the parent path first."),
                        };
                        EmitMgError(
                            rootId,
                            mgPath,
                            payload.Error,
                            branch: branch,
                            baseBranch: baseBranch,
                            depth: path.Depth);
                        return ExitCodes.RoutingFailure;
                    }

                    var result = new BranchEnsureMergeGroupResult
                    {
                        Branch = branch,
                        BaseBranch = baseBranch,
                        Action = outcome.Action,
                        RemoteExisted = outcome.RemoteExisted,
                        Pushed = outcome.Pushed,
                        BaseRemoteExisted = outcome.BaseRemoteExisted,
                        BaseFetched = outcome.BaseFetched,
                        CreatedFrom = outcome.CreatedFrom,
                        RootId = rootId,
                        MgPath = path.Canonical,
                        Depth = path.Depth,
                        DepthWarning = path.RequiresDepthWarning,
                        DepthExceeded = false,
                    };
                    payload = new BranchEnsureMergeGroupPayload
                    {
                        RootId = rootId,
                        MergeGroupPath = path.Canonical,
                        Depth = path.Depth,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = outcome.Action,
                        Succeeded = true,
                        WasMutated = outcome.WasMutated,
                        WasCreated = string.Equals(outcome.Action, "created", StringComparison.Ordinal),
                        WasPushed = outcome.Pushed,
                        BaseFetched = outcome.BaseFetched,
                        Sha = await _branchEnsurer.TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
                    };
                    EmitMergeGroup(result);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    payload = new BranchEnsureMergeGroupPayload
                    {
                        RootId = rootId,
                        MergeGroupPath = path.Canonical,
                        Depth = path.Depth,
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
                    EmitMgError(
                        rootId,
                        mgPath,
                        ex.Message,
                        branch: branch,
                        baseBranch: baseBranch,
                        depth: path.Depth);
                    return ExitCodes.CacheError;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.BranchEnsureMergeGroupPayload),
            effectsSelector: _ => SelectEnsureMergeGroupEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    private static void EmitMergeGroup(BranchEnsureMergeGroupResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult));

    private static void EmitMgError(
        int rootId,
        string mgPath,
        string message,
        string branch = "",
        string baseBranch = "",
        bool depthExceeded = false,
        int depth = 0)
    {
        var result = new BranchEnsureMergeGroupResult
        {
            Branch = branch,
            BaseBranch = baseBranch,
            Action = "error",
            RemoteExisted = false,
            Pushed = false,
            BaseRemoteExisted = false,
            BaseFetched = false,
            RootId = rootId,
            MgPath = mgPath,
            Depth = depth,
            DepthWarning = false,
            DepthExceeded = depthExceeded,
            Error = message,
        };
        EmitMergeGroup(result);
    }
}
