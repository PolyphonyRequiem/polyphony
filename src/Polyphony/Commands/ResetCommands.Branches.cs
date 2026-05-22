using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset branches --root N [--execute]</c> — deletes
/// every polyphony-scoped branch for the root on both origin and the
/// local repo: <c>plan/{N}</c>, nested <c>plan/{N}-*</c>, <c>mg/{N}-*</c>,
/// <c>impl/{N}-*</c>, <c>evidence/{N}-*</c>, <c>feature/{N}</c>, and
/// literal <c>sdlc/apex/{id}</c> branches for the root + descendants.
///
/// <para><b>Ordering</b>: runs AFTER <c>reset worktrees</c> so no local
/// branch is pinned by a checked-out worktree, and AFTER
/// <c>reset prs</c> so no live PR is left pointing at a vanishing
/// branch (the platform handles that fine, but the human-readable
/// audit trail is cleaner if PR-close logs go first).</para>
///
/// <para><b>Per-pattern enumeration</b>:
/// <list type="bullet">
///   <item>Origin: <c>git ls-remote --heads origin refs/heads/{pattern}</c>.</item>
///   <item>Local: <c>git for-each-ref --format=%(refname:short) refs/heads/{pattern}</c>.</item>
/// </list>
/// Both lists are de-duped and merged so a branch that exists only on
/// one side still gets the side-specific delete.</para>
///
/// <para><b>Failure tolerance</b>: per-branch failures (origin push
/// rejected, local branch refused for being checked out) are surfaced
/// as <see cref="ResetFailedBranch"/> entries; the verb still reports
/// <see cref="ResetBranchesResult.Success"/> = true. Hard failures
/// (ls-remote crashed for all patterns) set Success = false.</para>
///
/// <para><b>Dry-run</b> is the default. Pass <c>--execute</c> to delete.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Delete every root-scoped branch on origin and locally.
    /// </summary>
    /// <param name="root">Root root work-item ID.</param>
    /// <param name="execute">Pass to actually delete branches. Without this flag, the verb is dry-run.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("branches")]
    [VerbResult(typeof(ResetBranchesResult))]
    [JournaledAction(Action = "reset_branches")]
    [MutatesResource(ResourceKind.GitBranch)]
    public Task<int> ResetBranches(
        int root = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset branches",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return Task.FromResult(halt);

        return JournalCommandSupport.RunWithCapturedResultAsync<ResetBranchesResult, ResetBranchesPayload>(
            _journalDecorator,
            _runContext,
            "reset_branches",
            $"root:{root}",
            innerCt => ResetBranchesCoreAsync(root, execute, innerCt),
            PolyphonyJsonContext.Default.ResetBranchesResult,
            (_, result) => new ResetBranchesPayload
            {
                Root = result?.Root ?? root,
                DryRun = result?.DryRun ?? !execute,
                Succeeded = result?.Success ?? false,
                WasMutated = result is not null && !result.DryRun && result.DeletedBranches.Count > 0,
                DeletedBranches = result?.DeletedBranches ?? [],
                FailedBranches = result?.FailedBranches ?? [],
                Error = result?.Error,
            },
            PolyphonyJsonContext.Default.ResetBranchesPayload,
            payload => payload.Succeeded,
            payload => payload.WasMutated,
            SelectResetBranchesEffects,
            ct,
            rootId: root);
    }

    private async Task<int> ResetBranchesCoreAsync(
        int root,
        bool execute,
        CancellationToken ct)
    {
        ResetBranchesResult result;
        try
        {
            var perBranch = await EnumerateApexBranchesBySideAsync(root, ct).ConfigureAwait(false);

            var deleted = new List<ResetDeletedBranch>();
            var failed = new List<ResetFailedBranch>();

            foreach (var (branch, side) in perBranch)
            {
                ct.ThrowIfCancellationRequested();

                bool deletedRemote = false;
                bool deletedLocal = false;
                var failures = new List<string>();

                if (!execute)
                {
                    deleted.Add(new ResetDeletedBranch
                    {
                        Branch = branch,
                        DeletedRemote = side.HasFlag(BranchSide.Remote),
                        DeletedLocal = side.HasFlag(BranchSide.Local),
                    });
                    continue;
                }

                if (side.HasFlag(BranchSide.Remote))
                {
                    var r = await _git.DeleteRemoteBranchAsync("origin", branch, ct).ConfigureAwait(false);
                    if (r.Succeeded) deletedRemote = true;
                    else failures.Add($"remote: {r.Stderr.Trim()}");
                }
                if (side.HasFlag(BranchSide.Local))
                {
                    var l = await _git.DeleteLocalBranchAsync(branch, force: true, ct).ConfigureAwait(false);
                    if (l.Succeeded) deletedLocal = true;
                    else failures.Add($"local: {l.Stderr.Trim()}");
                }

                if (failures.Count == 0)
                {
                    deleted.Add(new ResetDeletedBranch
                    {
                        Branch = branch,
                        DeletedRemote = deletedRemote,
                        DeletedLocal = deletedLocal,
                    });
                }
                else
                {
                    failed.Add(new ResetFailedBranch
                    {
                        Branch = branch,
                        Scope = FormatScope(side),
                        Reason = string.Join("; ", failures),
                    });
                }
            }

            result = new ResetBranchesResult
            {
                Root = root,
                Success = true,
                DryRun = !execute,
                DeletedBranches = deleted,
                FailedBranches = failed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetBranchesResult
            {
                Root = root,
                Success = false,
                DryRun = !execute,
                DeletedBranches = [],
                FailedBranches = [],
                Error = $"Error resetting branches for root #{root}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    [Flags]
    private enum BranchSide
    {
        None = 0,
        Local = 1,
        Remote = 2,
        Both = Local | Remote,
    }

    private static string FormatScope(BranchSide side) => side switch
    {
        BranchSide.Both => "both",
        BranchSide.Local => "local",
        BranchSide.Remote => "remote",
        _ => "none",
    };

    /// <summary>
    /// Enumerate every root-scoped branch grouped by which side(s) it
    /// lives on. Order: pattern order; alphabetical within a pattern.
    /// De-dupes across patterns.
    /// </summary>
    private async Task<IReadOnlyList<(string Branch, BranchSide Side)>>
        EnumerateApexBranchesBySideAsync(int root, CancellationToken ct)
    {
        var sides = new Dictionary<string, BranchSide>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var pattern in RootBranchPatterns(root))
        {
            await AccumulateBranchesBySideAsync(pattern, sides, order, ct).ConfigureAwait(false);
        }

        foreach (var branch in await EnumerateApexSdlcBranchesAsync(root, ct).ConfigureAwait(false))
        {
            await AccumulateBranchesBySideAsync(branch, sides, order, ct).ConfigureAwait(false);
        }
 
        return order.Select(b => (b, sides[b])).ToList();
    }

    private async Task AccumulateBranchesBySideAsync(
        string pattern,
        Dictionary<string, BranchSide> sides,
        List<string> order,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<string> remoteRaw;
        try
        {
            remoteRaw = await _git.LsRemoteHeadsAsync(
                "origin", $"refs/heads/{pattern}", ct).ConfigureAwait(false);
        }
        catch (ExternalToolException)
        {
            remoteRaw = [];
        }

        foreach (var line in remoteRaw)
        {
            var idx = line.IndexOf("refs/heads/", StringComparison.Ordinal);
            if (idx < 0) continue;
            var name = line[(idx + "refs/heads/".Length)..].Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!sides.ContainsKey(name))
            {
                sides[name] = BranchSide.None;
                order.Add(name);
            }
            sides[name] |= BranchSide.Remote;
        }

        IReadOnlyList<string> local;
        try
        {
            local = await _git.ListLocalBranchesAsync(pattern, ct).ConfigureAwait(false);
        }
        catch (ExternalToolException)
        {
            local = [];
        }

        foreach (var name in local)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!sides.ContainsKey(name))
            {
                sides[name] = BranchSide.None;
                order.Add(name);
            }
            sides[name] |= BranchSide.Local;
        }
    }

    private async Task<IReadOnlyList<string>> EnumerateApexSdlcBranchesAsync(int apex, CancellationToken ct)
    {
        try
        {
            var hierarchy = await _walker.WalkAsync(apex, maxDepth: 8, ct).ConfigureAwait(false);
            if (hierarchy is null)
            {
                return [$"sdlc/apex/{apex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"];
            }

            var result = new List<string>();
            var seen = new HashSet<int>();

            void Collect(HierarchyResult node)
            {
                if (!seen.Add(node.WorkItemId))
                {
                    return;
                }

                result.Add($"sdlc/apex/{node.WorkItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

                if (node.Children is null)
                {
                    return;
                }

                foreach (var child in node.Children)
                {
                    Collect(child);
                }
            }

            Collect(hierarchy);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Emit(ResetBranchesResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetBranchesResult));
}
