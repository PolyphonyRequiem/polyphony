using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Idempotently ensure an evidence branch exists locally and on the
    /// remote. The branch name is built from the Rev 4 grammar via
    /// <see cref="BranchNameBuilder.Evidence(RootId, WorkItemId)"/> when
    /// <paramref name="apexId"/> differs from <paramref name="workItemId"/>,
    /// or via <see cref="BranchNameBuilder.EvidenceOrphan(WorkItemId)"/>
    /// when they match (the work item is its own apex).
    ///
    /// <para>The base branch defaults to <c>feature/{apex_id}</c>; pass
    /// <paramref name="fromRef"/> to override (e.g. branch off a sibling MG
    /// branch for layered evidence). If the base does not exist on the
    /// remote, the verb fails with <see cref="ExitCodes.RoutingFailure"/>.</para>
    /// </summary>
    /// <param name="workItemId">ADO work item id the evidence is for.</param>
    /// <param name="apexId">Apex (root) work-item id. Defaults to <paramref name="workItemId"/>; when equal, the orphan branch form <c>evidence/{item}</c> is used.</param>
    /// <param name="fromRef">Optional base branch override. When empty, defaults to <c>feature/{apex_id}</c>.</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-evidence-branch")]
    [VerbResult(typeof(BranchEnsureEvidenceResult))]
    public async Task<int> EnsureEvidenceBranch(
        int workItemId = RequiredInput.MissingInt,
        int apexId = 0,
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
            EmitEvidenceError(workItemId, apexId, fromRef, $"workItemId must be positive (got {workItemId})");
            return ExitCodes.ConfigError;
        }

        // Default apex to the work item itself (orphan evidence). A
        // negative explicit apex is a config error so callers don't get a
        // silent collapse to orphan when they meant to pass a real apex.
        if (apexId < 0)
        {
            EmitEvidenceError(workItemId, apexId, fromRef, $"apexId must be non-negative (got {apexId})");
            return ExitCodes.ConfigError;
        }

        var resolvedApexId = apexId == 0 ? workItemId : apexId;
        if (!RootId.TryParse(resolvedApexId, out var apex))
        {
            EmitEvidenceError(workItemId, apexId, fromRef, $"apexId must be positive (got {resolvedApexId})");
            return ExitCodes.ConfigError;
        }

        // ── 2. Resolve branch name (orphan vs combined) and base ref. ────
        var orphan = resolvedApexId == workItemId;
        var branch = orphan
            ? BranchNameBuilder.EvidenceOrphan(item).Value
            : BranchNameBuilder.Evidence(apex, item).Value;

        var baseBranch = string.IsNullOrEmpty(fromRef)
            ? BranchNameBuilder.Feature(apex).Value
            : fromRef;

        try
        {
            // ── 3. Inspect current state. ────────────────────────────────
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

                // Base is irrelevant when the target already exists locally,
                // but we still report whether it's on the remote so the
                // workflow can distinguish "evidence exists, but the apex
                // feature has been deleted" from a fully wired state.
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
                // Need to materialize from base. Confirm it exists on remote first.
                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
                if (!baseRemoteExisted)
                {
                    EmitEvidenceError(
                        workItemId,
                        apexId,
                        fromRef,
                        $"base branch '{baseBranch}' does not exist on remote '{remote}'. " +
                        (string.IsNullOrEmpty(fromRef)
                            ? $"Run 'polyphony branch ensure-feature' for apex {resolvedApexId} first, or pass --from-ref to base evidence on a different branch."
                            : "Verify the --from-ref value points at a branch that exists on the remote."),
                        branch: branch,
                        baseBranch: baseBranch,
                        orphan: orphan);
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
                ApexId = resolvedApexId,
                ItemId = workItemId,
                Orphan = orphan,
                FromRef = fromRef,
            };
            EmitEvidence(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitEvidenceError(
                workItemId,
                apexId,
                fromRef,
                ex.Message,
                branch: branch,
                baseBranch: baseBranch,
                orphan: orphan);
            return ExitCodes.CacheError;
        }
    }

    private static void EmitEvidence(BranchEnsureEvidenceResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult));

    private static void EmitEvidenceError(
        int workItemId,
        int apexId,
        string fromRef,
        string message,
        string branch = "",
        string baseBranch = "",
        bool orphan = false)
    {
        var resolvedApexId = apexId == 0 ? workItemId : apexId;
        var result = new BranchEnsureEvidenceResult
        {
            Branch = branch,
            BaseBranch = baseBranch,
            Action = "error",
            RemoteExisted = false,
            Pushed = false,
            BaseRemoteExisted = false,
            BaseFetched = false,
            ApexId = resolvedApexId,
            ItemId = workItemId,
            Orphan = orphan,
            FromRef = fromRef,
            Error = message,
        };
        EmitEvidence(result);
    }
}
