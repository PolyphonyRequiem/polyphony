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
    /// Idempotently ensure a feature branch exists locally and on the remote.
    /// Creates from <paramref name="baseBranch"/> if absent. The root workflow
    /// calls this once after state detection; sub-workflows receive the branch
    /// name as an input and trust it.
    /// </summary>
    /// <param name="branch">Feature branch name (e.g. feature/2943-my-epic).</param>
    /// <param name="baseBranch">Branch to create from if the feature branch doesn't exist.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-feature")]
    [JournaledAction(Action = "branch_ensure_feature")]
    [MutatesResource(ResourceKind.GitBranch)]
    [VerbResult(typeof(BranchEnsureFeatureResult))]
    public async Task<int> EnsureFeature(
        string branch = "",
        string baseBranch = "main",
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch ensure-feature",
            ("--branch", string.IsNullOrEmpty(branch))) is { } halt)
            return halt;

        var parsedRootId = TryParseFeatureRootId(branch);
        BranchEnsureFeaturePayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("branch_ensure_feature", branch, parsedRootId, parsedRootId),
            async innerCt =>
            {
                try
                {
                    // Feature branches tolerate the AB#211 sibling-worktree
                    // case and trust the base branch (main) without an
                    // ls-remote probe. See BranchEnsurer for the matrix.
                    var outcome = await _branchEnsurer.EnsureAsync(
                        new BranchSpec(
                            Target: branch,
                            Base: baseBranch,
                            Remote: remote,
                            TolerateWorktreeConflict: true,
                            IncludeBaseOnRemoteCheck: false),
                        innerCt).ConfigureAwait(false);

                    var result = new BranchEnsureFeatureResult
                    {
                        Branch = branch,
                        Action = outcome.Action,
                        RemoteExisted = outcome.RemoteExisted,
                        Pushed = outcome.Pushed,
                        CreatedFrom = outcome.CreatedFrom,
                        WorktreePath = outcome.WorktreePath,
                    };
                    payload = new BranchEnsureFeaturePayload
                    {
                        RootId = parsedRootId,
                        WorkItemId = parsedRootId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = outcome.Action,
                        Succeeded = true,
                        WasMutated = outcome.WasMutated,
                        WasCreated = string.Equals(outcome.Action, "created", StringComparison.Ordinal),
                        WasPushed = outcome.Pushed,
                        WorktreePath = outcome.WorktreePath,
                        Sha = await _branchEnsurer.TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
                    };
                    Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.BranchEnsureFeatureResult));
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    payload = new BranchEnsureFeaturePayload
                    {
                        RootId = parsedRootId,
                        WorkItemId = parsedRootId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        WasCreated = false,
                        WasPushed = false,
                        Error = ex.Message,
                    };
                    var result = new BranchEnsureFeatureResult
                    {
                        Branch = branch,
                        Action = "error",
                        RemoteExisted = false,
                        Pushed = false,
                        Error = ex.Message,
                    };
                    Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.BranchEnsureFeatureResult));
                    return ExitCodes.CacheError;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.BranchEnsureFeaturePayload),
            effectsSelector: _ => SelectEnsureFeatureEffects(payload),
            ct: ct).ConfigureAwait(false);
    }
}
