using System.Text.RegularExpressions;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Branching;

/// <summary>
/// Shared idempotent branch-ensure matrix used by every
/// <c>BranchCommands.Ensure*</c> verb (feature, plan, impl, merge-group,
/// evidence). Each verb owns its own input validation, journal envelope,
/// per-kind error guidance, and result-envelope projection; the matrix
/// itself — ls-remote / rev-parse / checkout / fetch+track / create+push
/// — lives here so the five verbs never diverge.
///
/// <para>This is a behavior-preserving refactor (AB#3312). The git
/// invocation sequence for each branch of the matrix matches the
/// pre-refactor verbs exactly. The five existing per-verb test files
/// stub <c>IGitClient</c> via <c>FakeProcessRunner</c> and act as the
/// safety net for that promise.</para>
/// </summary>
public sealed partial class BranchEnsurer(IGitClient git)
{
    /// <summary>
    /// Matches git's "fatal: '&lt;branch&gt;' is already used by worktree at
    /// '&lt;path&gt;'" stderr (AB#211). Single-quoted on POSIX; git emits
    /// the same quoting on Windows. Captures the worktree path so the
    /// ensurer can surface it in the success outcome.
    /// </summary>
    [GeneratedRegex(
        @"is already used by worktree at '([^']+)'",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BranchInOtherWorktreeRegex();

    /// <summary>
    /// Idempotently ensure <paramref name="spec"/>.Target exists locally
    /// and on the remote. Returns either <see cref="BranchEnsureStatus.Success"/>
    /// with a populated outcome, or <see cref="BranchEnsureStatus.BaseMissingOnRemote"/>
    /// when the base branch is required but absent. Per-kind error hint
    /// text is the verb's responsibility — this method reports the
    /// failure shape, not the user-facing message.
    /// </summary>
    public async Task<BranchEnsureOutcome> EnsureAsync(BranchSpec spec, CancellationToken ct)
    {
        // 1. Probe remote + local existence of the target branch.
        var remoteRefs = await git.LsRemoteHeadsAsync(spec.Remote, spec.Target, ct).ConfigureAwait(false);
        var remoteExisted = remoteRefs.Count > 0;

        var localSha = await git.RevParseLocalBranchAsync(spec.Target, ct).ConfigureAwait(false);
        var localExisted = localSha is not null;
        var currentBranch = localExisted
            ? await TryGetCurrentBranchAsync(ct).ConfigureAwait(false)
            : null;

        string action;
        bool pushed = false;
        string? createdFrom = null;
        string? worktreePath = null;
        bool baseRemoteExisted = false;
        bool baseFetched = false;
        bool wasMutated;

        if (localExisted)
        {
            // Local exists — check it out. Worktree-conflict tolerance
            // (AB#211) is opt-in via spec.TolerateWorktreeConflict; other
            // kinds let the exception propagate to the verb's catch block.
            try
            {
                await git.CheckoutAsync(spec.Target, ct).ConfigureAwait(false);
                action = "checked_out";
                wasMutated = currentBranch is null
                    || !string.Equals(currentBranch, spec.Target, StringComparison.Ordinal);
            }
            catch (ExternalToolException ex)
                when (spec.TolerateWorktreeConflict
                      && BranchInOtherWorktreeRegex().Match(ex.Stderr) is { Success: true } match)
            {
                worktreePath = match.Groups[1].Value;
                action = "exists_in_other_worktree";
                wasMutated = false;
            }

            if (!remoteExisted)
            {
                // Push so downstream steps can branch from it. Push works
                // regardless of which worktree owns the checkout — git
                // resolves refs/heads/{branch} by ref, not by working tree.
                await git.PushAsync(spec.Target, spec.Remote, ct).ConfigureAwait(false);
                pushed = true;
                wasMutated = true;
            }

            if (spec.IncludeBaseOnRemoteCheck)
            {
                baseRemoteExisted = await BaseExistsOnRemoteAsync(spec.Base!, spec.Remote, ct).ConfigureAwait(false);
            }
        }
        else if (remoteExisted)
        {
            // Remote exists, local doesn't — fetch + track.
            await git.FetchAsync(spec.Remote, spec.Target, ct).ConfigureAwait(false);
            await git.CheckoutTrackingAsync(spec.Target, spec.Remote, ct).ConfigureAwait(false);
            action = "checked_out";
            wasMutated = true;

            if (spec.IncludeBaseOnRemoteCheck)
            {
                baseRemoteExisted = await BaseExistsOnRemoteAsync(spec.Base!, spec.Remote, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Neither exists — materialize from base.
            if (spec.IncludeBaseOnRemoteCheck)
            {
                baseRemoteExisted = await BaseExistsOnRemoteAsync(spec.Base!, spec.Remote, ct).ConfigureAwait(false);
                if (!baseRemoteExisted)
                {
                    return BranchEnsureOutcome.BaseMissing(spec.Base!, spec.Remote);
                }

                // If the base isn't local, fetch and check it out so the
                // create-from-base step has a known local start point.
                var baseLocalSha = await git.RevParseLocalBranchAsync(spec.Base!, ct).ConfigureAwait(false);
                if (baseLocalSha is null)
                {
                    await git.FetchAsync(spec.Remote, spec.Base!, ct).ConfigureAwait(false);
                    await git.CheckoutTrackingAsync(spec.Base!, spec.Remote, ct).ConfigureAwait(false);
                    baseFetched = true;
                }
            }

            await git.CreateBranchAsync(spec.Target, spec.Base, ct).ConfigureAwait(false);
            await git.PushAsync(spec.Target, spec.Remote, ct).ConfigureAwait(false);
            action = "created";
            pushed = true;
            createdFrom = spec.Base;
            wasMutated = true;
        }

        return BranchEnsureOutcome.Success(
            action: action,
            remoteExisted: remoteExisted,
            pushed: pushed,
            createdFrom: createdFrom,
            worktreePath: worktreePath,
            baseRemoteExisted: baseRemoteExisted,
            baseFetched: baseFetched,
            wasMutated: wasMutated);
    }

    /// <summary>
    /// Returns the SHA of <paramref name="branch"/> via
    /// <c>git rev-parse --verify refs/heads/{branch}</c>, or null if the
    /// rev-parse fails for any reason. Convenience helper exposed for
    /// verbs that populate <c>Payload.Sha</c> after the matrix completes.
    /// </summary>
    public async Task<string?> TryGetBranchShaAsync(string branch, CancellationToken ct)
    {
        try
        {
            return await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryGetCurrentBranchAsync(CancellationToken ct)
    {
        try
        {
            return await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> BaseExistsOnRemoteAsync(string baseBranch, string remote, CancellationToken ct)
    {
        var refs = await git.LsRemoteHeadsAsync(remote, baseBranch, ct).ConfigureAwait(false);
        return refs.Count > 0;
    }
}

/// <summary>
/// Inputs to <see cref="BranchEnsurer.EnsureAsync"/>.
/// </summary>
/// <param name="Target">Branch to ensure exists locally + remote.</param>
/// <param name="Base">Branch to create <paramref name="Target"/> from
///   when neither local nor remote copy of <paramref name="Target"/>
///   exists. Required when <paramref name="IncludeBaseOnRemoteCheck"/>
///   is true; may be the literal trusted value (e.g. <c>main</c>) when
///   the caller opts out of the remote check.</param>
/// <param name="Remote">Git remote name (default <c>origin</c>).</param>
/// <param name="TolerateWorktreeConflict">When true, treat
///   "branch already used by another worktree" as a non-failure
///   outcome and surface the worktree path. Used by the feature-branch
///   verb (AB#211); other verbs throw on this condition.</param>
/// <param name="IncludeBaseOnRemoteCheck">When true, the matrix
///   validates that <paramref name="Base"/> exists on the remote
///   before creating <paramref name="Target"/> from it AND populates
///   <c>BaseRemoteExisted</c> on the outcome. When false the matrix
///   trusts <paramref name="Base"/> exists (feature-branch behavior;
///   base is <c>main</c>).</param>
public sealed record BranchSpec(
    string Target,
    string? Base,
    string Remote,
    bool TolerateWorktreeConflict = false,
    bool IncludeBaseOnRemoteCheck = true);

/// <summary>
/// Outcome of <see cref="BranchEnsurer.EnsureAsync"/>. Use the static
/// factories <see cref="Success"/> and <see cref="BaseMissing"/> rather
/// than constructing directly — the success/failure shapes have
/// mutually-exclusive fields and the factories prevent invalid states.
/// </summary>
public sealed record BranchEnsureOutcome
{
    public BranchEnsureStatus Status { get; init; }

    /// <summary>"created", "checked_out", "exists_in_other_worktree", or "error".</summary>
    public string Action { get; init; } = "error";

    public bool RemoteExisted { get; init; }
    public bool Pushed { get; init; }
    public string? CreatedFrom { get; init; }

    /// <summary>Populated only when <see cref="Action"/> is
    /// <c>exists_in_other_worktree</c>.</summary>
    public string? WorktreePath { get; init; }

    public bool BaseRemoteExisted { get; init; }
    public bool BaseFetched { get; init; }
    public bool WasMutated { get; init; }

    /// <summary>Echoed back from <see cref="BranchSpec.Base"/> when
    /// <see cref="Status"/> is <see cref="BranchEnsureStatus.BaseMissingOnRemote"/>.</summary>
    public string? MissingBase { get; init; }

    /// <summary>Echoed back from <see cref="BranchSpec.Remote"/> when
    /// <see cref="Status"/> is <see cref="BranchEnsureStatus.BaseMissingOnRemote"/>.</summary>
    public string? MissingBaseRemote { get; init; }

    private BranchEnsureOutcome() { }

    public static BranchEnsureOutcome Success(
        string action,
        bool remoteExisted,
        bool pushed,
        string? createdFrom,
        string? worktreePath,
        bool baseRemoteExisted,
        bool baseFetched,
        bool wasMutated)
        => new()
        {
            Status = BranchEnsureStatus.Success,
            Action = action,
            RemoteExisted = remoteExisted,
            Pushed = pushed,
            CreatedFrom = createdFrom,
            WorktreePath = worktreePath,
            BaseRemoteExisted = baseRemoteExisted,
            BaseFetched = baseFetched,
            WasMutated = wasMutated,
        };

    public static BranchEnsureOutcome BaseMissing(string baseBranch, string remote)
        => new()
        {
            Status = BranchEnsureStatus.BaseMissingOnRemote,
            Action = "error",
            MissingBase = baseBranch,
            MissingBaseRemote = remote,
        };
}

public enum BranchEnsureStatus
{
    Success,
    BaseMissingOnRemote,
}
