using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Worktrees;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset worktrees --root N [--execute]</c> — removes
/// every git worktree rooted under <c>{runs_root}/root-{N}/</c> and
/// then removes the (now-empty) root directory itself.
///
/// <para>Why this exists at all (vs. just letting the workflow re-use
/// worktrees): a partial run can leave the worktree's branch in a state
/// that <c>git worktree add</c> on the next run refuses to overwrite,
/// and detached worktrees pin local branches that
/// <c>reset branches</c> would otherwise sweep. Removing the whole
/// <c>root-{N}/</c> tree gives the redispatch a clean slate.</para>
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
/// <see cref="ResetWorktreesResult.RootDirDeleted"/> = false; the
/// verb still succeeds.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Remove every root-scoped git worktree.
    /// </summary>
    /// <param name="root">Root root work-item ID. Used to compute <c>{runs_root}/root-{N}/</c>.</param>
    /// <param name="execute">Pass to actually remove worktrees. Without this flag, the verb is dry-run.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("worktrees")]
    [VerbResult(typeof(ResetWorktreesResult))]
    [JournaledAction(Action = "reset_worktrees")]
    [MutatesResource(ResourceKind.GitWorktree)]
    public Task<int> ResetWorktrees(
        int root = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset worktrees",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return Task.FromResult(halt);

        return JournalCommandSupport.RunWithCapturedResultAsync<ResetWorktreesResult, ResetWorktreesPayload>(
            _journalDecorator,
            _runContext,
            "reset_worktrees",
            $"root:{root}",
            innerCt => ResetWorktreesCoreAsync(root, execute, innerCt),
            PolyphonyJsonContext.Default.ResetWorktreesResult,
            (_, result) => new ResetWorktreesPayload
            {
                Root = result?.Root ?? root,
                DryRun = result?.DryRun ?? !execute,
                Succeeded = result?.Success ?? false,
                WasMutated = result is not null && !result.DryRun && (result.RemovedWorktrees.Count > 0 || result.RootDirDeleted),
                RootRunsRoot = result?.RootRunsRoot ?? string.Empty,
                RemovedWorktrees = result?.RemovedWorktrees ?? [],
                FailedWorktrees = result?.FailedWorktrees ?? [],
                RootDirDeleted = result?.RootDirDeleted ?? false,
                Error = result?.Error,
            },
            PolyphonyJsonContext.Default.ResetWorktreesPayload,
            payload => payload.Succeeded,
            payload => payload.WasMutated,
            SelectResetWorktreesEffects,
            ct,
            rootId: root);
    }

    private async Task<int> ResetWorktreesCoreAsync(
        int root,
        bool execute,
        CancellationToken ct)
    {
        ResetWorktreesResult result;
        try
        {
            var commonDir = await _git.GetCommonDirAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "git rev-parse --git-common-dir returned empty; not inside a git repo?");

            var (runsRoot, _) = RunsRootResolver.Resolve(commonDir);
            var rootRunsRoot = Path.Combine(runsRoot, $"root-{root}");

            var listResult = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!listResult.Succeeded)
            {
                result = new ResetWorktreesResult
                {
                    Root = root,
                    Success = false,
                    DryRun = !execute,
                    RootRunsRoot = rootRunsRoot,
                    RemovedWorktrees = [],
                    FailedWorktrees = [],
                    RootDirDeleted = false,
                    Error =
                        $"git worktree list --porcelain failed (exit {listResult.ExitCode}): " +
                        $"{listResult.Stderr.Trim()}",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var entries = WorktreeCommands.ParsePorcelain(listResult.Stdout);
            var rootEntries = entries
                .Where(e => !string.IsNullOrEmpty(e.Path)
                            && PathBoundary.IsSameOrSubpath(rootRunsRoot, e.Path))
                .ToList();

            var removed = new List<ResetRemovedWorktree>();
            var failed = new List<ResetFailedWorktree>();

            foreach (var entry in rootEntries)
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
            // down the root-{N} directory itself so the next dispatch
            // starts clean. Dry-run skips the delete but still reports
            // whether the directory currently exists.
            bool dirDeleted = false;
            if (Directory.Exists(rootRunsRoot))
            {
                if (execute)
                {
                    dirDeleted = await TryDeleteDirectoryWithRetryAsync(rootRunsRoot, ct).ConfigureAwait(false);
                }
                // Dry-run: dirDeleted stays false, RootDirExists implicit.
            }
            else
            {
                // Already absent.
                dirDeleted = true;
            }

            result = new ResetWorktreesResult
            {
                Root = root,
                Success = true,
                DryRun = !execute,
                RootRunsRoot = rootRunsRoot,
                RemovedWorktrees = removed,
                FailedWorktrees = failed,
                RootDirDeleted = dirDeleted,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetWorktreesResult
            {
                Root = root,
                Success = false,
                DryRun = !execute,
                RootRunsRoot = string.Empty,
                RemovedWorktrees = [],
                FailedWorktrees = [],
                RootDirDeleted = false,
                Error = $"Error resetting worktrees for root #{root}: {ex.Message}",
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
