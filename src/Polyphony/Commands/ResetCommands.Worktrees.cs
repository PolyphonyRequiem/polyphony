using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Worktrees;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset worktrees --apex N [--execute]</c> — removes
/// every git worktree rooted under <c>{runs_root}/apex-{N}/</c> and
/// then removes the (now-empty) apex directory itself.
///
/// <para>Why this exists at all (vs. just letting the workflow re-use
/// worktrees): a partial run can leave the worktree's branch in a state
/// that <c>git worktree add</c> on the next run refuses to overwrite,
/// and detached worktrees pin local branches that
/// <c>reset branches</c> would otherwise sweep. Removing the whole
/// <c>apex-{N}/</c> tree gives the redispatch a clean slate.</para>
///
/// <para><b>Ordering</b> (per <c>docs/decisions/run-reset.md</c>): runs
/// AFTER <c>reset prs</c> and BEFORE <c>reset branches</c>, so that
/// branch deletion finds no checked-out worktree pinning the branch.</para>
///
/// <para><b>Force removal</b>: every worktree is removed with
/// <c>--force</c>. Polyphony worktrees are scratch space — losing
/// uncommitted changes inside a reset operation is the documented
/// contract. Operators who want to preserve in-progress work must
/// abort the reset BEFORE running this verb.</para>
///
/// <para><b>Failure tolerance</b>: a worktree that git refuses to
/// remove (locked, missing, permissions) is surfaced as a
/// <see cref="ResetFailedWorktree"/> entry; the verb still reports
/// <see cref="ResetWorktreesResult.Success"/> = true. Directory deletion
/// failure (after worktrees gone) is tolerated and reported via
/// <see cref="ResetWorktreesResult.ApexDirDeleted"/> = false; the
/// verb still succeeds.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Remove every apex-scoped git worktree.
    /// </summary>
    /// <param name="apex">Apex root work-item ID. Used to compute <c>{runs_root}/apex-{N}/</c>.</param>
    /// <param name="execute">Pass to actually remove worktrees. Without this flag, the verb is dry-run.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("worktrees")]
    [VerbResult(typeof(ResetWorktreesResult))]
    public async Task<int> ResetWorktrees(
        int apex = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset worktrees",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        ResetWorktreesResult result;
        try
        {
            var commonDir = await _git.GetCommonDirAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "git rev-parse --git-common-dir returned empty; not inside a git repo?");

            var (runsRoot, _) = RunsRootResolver.Resolve(commonDir);
            var apexRunsRoot = Path.Combine(runsRoot, $"apex-{apex}");

            var listResult = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!listResult.Succeeded)
            {
                result = new ResetWorktreesResult
                {
                    Apex = apex,
                    Success = false,
                    DryRun = !execute,
                    ApexRunsRoot = apexRunsRoot,
                    RemovedWorktrees = [],
                    FailedWorktrees = [],
                    ApexDirDeleted = false,
                    Error =
                        $"git worktree list --porcelain failed (exit {listResult.ExitCode}): " +
                        $"{listResult.Stderr.Trim()}",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var entries = WorktreeCommands.ParsePorcelain(listResult.Stdout);
            var apexEntries = entries
                .Where(e => !string.IsNullOrEmpty(e.Path)
                            && PathBoundary.IsSameOrSubpath(apexRunsRoot, e.Path))
                .ToList();

            var removed = new List<ResetRemovedWorktree>();
            var failed = new List<ResetFailedWorktree>();

            foreach (var entry in apexEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (!execute)
                {
                    removed.Add(new ResetRemovedWorktree
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                    });
                    continue;
                }

                var (worktreeRemoved, lastError) = await TryRemoveWorktreeWithRetryAsync(entry.Path, ct)
                    .ConfigureAwait(false);
                if (worktreeRemoved)
                {
                    removed.Add(new ResetRemovedWorktree
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                    });
                }
                else
                {
                    failed.Add(new ResetFailedWorktree
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                        Reason = lastError ?? "git worktree remove failed after retries.",
                    });
                }
            }

            if (execute)
            {
                try
                {
                    await _git.WorktreePruneAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Best-effort cleanup only.
                }
            }

            // After all worktrees are gone (or in dry-run mode), tear
            // down the apex-{N} directory itself so the next dispatch
            // starts clean. Dry-run skips the delete but still reports
            // whether the directory currently exists.
            bool dirDeleted = false;
            if (Directory.Exists(apexRunsRoot))
            {
                if (execute)
                {
                    dirDeleted = await TryDeleteDirectoryWithRetryAsync(apexRunsRoot, ct).ConfigureAwait(false);
                }
                // Dry-run: dirDeleted stays false, ApexDirExists implicit.
            }
            else
            {
                // Already absent.
                dirDeleted = true;
            }

            result = new ResetWorktreesResult
            {
                Apex = apex,
                Success = true,
                DryRun = !execute,
                ApexRunsRoot = apexRunsRoot,
                RemovedWorktrees = removed,
                FailedWorktrees = failed,
                ApexDirDeleted = dirDeleted,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetWorktreesResult
            {
                Apex = apex,
                Success = false,
                DryRun = !execute,
                ApexRunsRoot = string.Empty,
                RemovedWorktrees = [],
                FailedWorktrees = [],
                ApexDirDeleted = false,
                Error = $"Error resetting worktrees for apex #{apex}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    private static readonly TimeSpan[] RemoveRetryBackoff =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
    ];

    private async Task<(bool removed, string? lastError)> TryRemoveWorktreeWithRetryAsync(
        string path,
        CancellationToken ct)
    {
        string? lastError = null;

        for (var attempt = 0; attempt < RemoveRetryBackoff.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var removeResult = await _git.WorktreeRemoveAsync(path, force: true, ct)
                .ConfigureAwait(false);
            if (removeResult.Succeeded)
            {
                return (true, null);
            }

            lastError = $"git worktree remove exited {removeResult.ExitCode}: {removeResult.Stderr.Trim()}";
            if (!Directory.Exists(path))
            {
                return (true, null);
            }

            await Task.Delay(RemoveRetryBackoff[attempt], ct).ConfigureAwait(false);
            if (!Directory.Exists(path))
            {
                return (true, null);
            }
        }

        var finalExists = Directory.Exists(path);
        return (!finalExists, finalExists ? lastError : null);
    }

    private static async Task<bool> TryDeleteDirectoryWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < RemoveRetryBackoff.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Race with another process holding a file handle; retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Another process may still be releasing a handle; retry.
            }

            await Task.Delay(RemoveRetryBackoff[attempt], ct).ConfigureAwait(false);
        }

        return !Directory.Exists(path);
    }

    private static void Emit(ResetWorktreesResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetWorktreesResult));
}
