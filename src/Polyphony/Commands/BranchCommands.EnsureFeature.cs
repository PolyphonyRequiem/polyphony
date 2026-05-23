using System.Text.Json;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Matches git's "fatal: '&lt;branch&gt;' is already used by worktree at
    /// '&lt;path&gt;'" stderr (AB#211). Single-quoted on POSIX; git emits
    /// the same quoting on Windows. Captures the worktree path so the
    /// verb can surface it in the success envelope.
    /// </summary>
    [GeneratedRegex(
        @"is already used by worktree at '([^']+)'",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BranchInOtherWorktreeRegex();

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
                    // 1. Check if the branch exists on the remote.
                    var remoteRefs = await git.LsRemoteHeadsAsync(remote, branch, innerCt).ConfigureAwait(false);
                    var remoteExisted = remoteRefs.Count > 0;

                    // 2. Check if it exists locally.
                    var localSha = await git.RevParseLocalBranchAsync(branch, innerCt).ConfigureAwait(false);
                    var localExisted = localSha is not null;
                    var currentBranch = localExisted
                        ? await TryGetCurrentBranchAsync(innerCt).ConfigureAwait(false)
                        : null;

                    if (localExisted || remoteExisted)
                    {
                        var lineageReason = await CheckBranchAdoptionLineageAsync(
                            "branch_ensure_feature", branch, innerCt).ConfigureAwait(false);
                        if (lineageReason is not null)
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
                                Error = "foreign_lineage: " + lineageReason,
                            };
                            var foreignResult = new BranchEnsureFeatureResult
                            {
                                Branch = branch,
                                Action = "error",
                                RemoteExisted = remoteExisted,
                                Pushed = false,
                                Error = payload.Error,
                            };
                            Console.WriteLine(JsonSerializer.Serialize(foreignResult, PolyphonyJsonContext.Default.BranchEnsureFeatureResult));
                            return ExitCodes.RoutingFailure;
                        }
                    }

                    string action;
                    bool pushed = false;
                    string? createdFrom = null;
                    string? worktreePath = null;
                    bool wasMutated;

                    if (localExisted)
                    {
                        // Local branch exists — try to check it out in the current
                        // worktree. Under the parallel-fleet root convention the
                        // branch may already be checked out in a sibling worktree;
                        // git refuses with exit 128 + "is already used by worktree
                        // at '...'". That is NOT a failure of this verb's purpose
                        // (the branch DOES exist locally); treat it as a success
                        // and surface the sibling worktree path so the workflow
                        // can route to it (AB#211).
                        try
                        {
                            await git.CheckoutAsync(branch, innerCt).ConfigureAwait(false);
                            action = "checked_out";
                            wasMutated = currentBranch is null || !string.Equals(currentBranch, branch, StringComparison.Ordinal);
                        }
                        catch (ExternalToolException ex)
                            when (BranchInOtherWorktreeRegex().Match(ex.Stderr) is { Success: true } worktreeMatch)
                        {
                            worktreePath = worktreeMatch.Groups[1].Value;
                            action = "exists_in_other_worktree";
                            wasMutated = false;
                        }

                        if (!remoteExisted)
                        {
                            // Push to remote so downstream steps can branch from
                            // it. Push works regardless of which worktree owns
                            // the checkout — git resolves refs/heads/{branch} by
                            // ref, not by working tree.
                            await git.PushAsync(branch, remote, innerCt).ConfigureAwait(false);
                            pushed = true;
                            wasMutated = true;
                        }
                    }
                    else if (remoteExisted)
                    {
                        // Remote exists but not local — fetch and create tracking branch.
                        await git.FetchAsync(remote, branch, innerCt).ConfigureAwait(false);
                        await git.CheckoutTrackingAsync(branch, remote, innerCt).ConfigureAwait(false);
                        action = "checked_out";
                        wasMutated = true;
                    }
                    else
                    {
                        // Neither local nor remote — create from base branch.
                        await git.CreateBranchAsync(branch, baseBranch, innerCt).ConfigureAwait(false);
                        await git.PushAsync(branch, remote, innerCt).ConfigureAwait(false);
                        action = "created";
                        pushed = true;
                        createdFrom = baseBranch;
                        wasMutated = true;
                    }

                    var result = new BranchEnsureFeatureResult
                    {
                        Branch = branch,
                        Action = action,
                        RemoteExisted = remoteExisted,
                        Pushed = pushed,
                        CreatedFrom = createdFrom,
                        WorktreePath = worktreePath,
                    };
                    payload = new BranchEnsureFeaturePayload
                    {
                        RootId = parsedRootId,
                        WorkItemId = parsedRootId,
                        BranchName = branch,
                        BaseBranch = baseBranch,
                        ResultAction = action,
                        Succeeded = true,
                        WasMutated = wasMutated,
                        WasCreated = string.Equals(action, "created", StringComparison.Ordinal),
                        WasPushed = pushed,
                        WorktreePath = worktreePath,
                        Sha = await TryGetBranchShaAsync(branch, innerCt).ConfigureAwait(false),
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

