using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

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
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path (each segment matches <c>^[a-z][a-z0-9-]{0,30}$</c>).</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("ensure-mg")]
    public async Task<int> EnsureMg(
        int rootId,
        string mgPath,
        string remote = "origin",
        CancellationToken ct = default)
    {
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

        try
        {
            // ── 2. Check current state of MG branch on remote and locally. ─
            var remoteRefs = await git.LsRemoteHeadsAsync(remote, branch, ct).ConfigureAwait(false);
            var remoteExisted = remoteRefs.Count > 0;

            var localSha = await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
            var localExisted = localSha is not null;

            string action;
            bool pushed = false;
            string? createdFrom = null;

            // ── 3. Verify the base branch exists on the remote — if it
            //      doesn't, child creation can't succeed. We check this
            //      only when we'd actually need it (target branch missing
            //      both locally and remotely). ─────────────────────────────
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

                // Base is irrelevant when the target already exists.
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
                // Need to materialize from base. Confirm base exists on remote first.
                baseRemoteExisted = await BaseExistsOnRemoteAsync(baseBranch, remote, ct).ConfigureAwait(false);
                if (!baseRemoteExisted)
                {
                    EmitMgError(
                        rootId,
                        mgPath,
                        $"base branch '{baseBranch}' does not exist on remote '{remote}'. " +
                        (path.IsTopLevel
                            ? "Run 'polyphony branch ensure-feature' first to create the feature branch."
                            : "Run 'polyphony branch ensure-mg' for the parent path first."),
                        branch: branch,
                        baseBranch: baseBranch,
                        depth: path.Depth);
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

            var result = new BranchEnsureMergeGroupResult
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
                MgPath = path.Canonical,
                Depth = path.Depth,
                DepthWarning = path.RequiresDepthWarning,
                DepthExceeded = false,
            };
            EmitMg(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitMgError(
                rootId,
                mgPath,
                ex.Message,
                branch: branch,
                baseBranch: baseBranch,
                depth: path.Depth);
            return ExitCodes.CacheError;
        }
    }

    private async Task<bool> BaseExistsOnRemoteAsync(string baseBranch, string remote, CancellationToken ct)
    {
        var refs = await git.LsRemoteHeadsAsync(remote, baseBranch, ct).ConfigureAwait(false);
        return refs.Count > 0;
    }

    private static void EmitMg(BranchEnsureMergeGroupResult result)
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
        EmitMg(result);
    }
}
