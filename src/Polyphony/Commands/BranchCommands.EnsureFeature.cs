using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

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

        try
        {
            // 1. Check if the branch exists on the remote.
            var remoteRefs = await git.LsRemoteHeadsAsync(remote, branch, ct).ConfigureAwait(false);
            var remoteExisted = remoteRefs.Count > 0;

            // 2. Check if it exists locally.
            var localSha = await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
            var localExisted = localSha is not null;

            string action;
            bool pushed = false;
            string? createdFrom = null;

            if (localExisted)
            {
                // Local branch exists — just check it out.
                await git.CheckoutAsync(branch, ct).ConfigureAwait(false);
                action = "checked_out";

                if (!remoteExisted)
                {
                    // Push to remote so downstream steps can branch from it.
                    await git.PushAsync(branch, remote, ct).ConfigureAwait(false);
                    pushed = true;
                }
            }
            else if (remoteExisted)
            {
                // Remote exists but not local — fetch and create tracking branch.
                await git.FetchAsync(remote, branch, ct).ConfigureAwait(false);
                await git.CheckoutTrackingAsync(branch, remote, ct).ConfigureAwait(false);
                action = "checked_out";
            }
            else
            {
                // Neither local nor remote — create from base branch.
                await git.CreateBranchAsync(branch, baseBranch, ct).ConfigureAwait(false);
                await git.PushAsync(branch, remote, ct).ConfigureAwait(false);
                action = "created";
                pushed = true;
                createdFrom = baseBranch;
            }

            var result = new BranchEnsureFeatureResult
            {
                Branch = branch,
                Action = action,
                RemoteExisted = remoteExisted,
                Pushed = pushed,
                CreatedFrom = createdFrom,
            };
            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.BranchEnsureFeatureResult));
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
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
    }
}

