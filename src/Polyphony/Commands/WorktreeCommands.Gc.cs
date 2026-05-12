using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Worktrees;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree gc</c> — prune stale per-run worktrees under
/// <c>polyphony-runs/</c>. Scope is always restricted to the per-run
/// worktree tree resolved from <c>git rev-parse --git-common-dir</c>;
/// the operator's main worktree and any sibling worktrees outside
/// <c>polyphony-runs/</c> are NEVER touched.
///
/// <para>Two prune categories:</para>
/// <list type="bullet">
///   <item><c>directory_missing</c> — git lists the worktree but its directory is gone (operator deleted it).</item>
///   <item><c>branch_deleted</c> — the worktree's branch is no longer present locally (a feature/* / impl/* / mg/* etc. that was merged + deleted).</item>
/// </list>
///
/// <para>Default mode is dry-run (lists candidates, no mutation). Pass
/// <c>--commit</c> to actually remove via <c>git worktree remove --force</c>.</para>
///
/// <para>Routing-style verb: ALWAYS exits 0; consumers branch on
/// <c>output.error</c> (resolution failure) and <c>output.failed_count</c>
/// (per-candidate removal failures during commit).</para>
///
/// <para>This verb intentionally does NOT touch:</para>
/// <list type="bullet">
///   <item>The operator's main worktree.</item>
///   <item>Sibling-of-main worktrees from the legacy layout (operators clean those manually during the AB#3085 transition).</item>
///   <item>Any worktree currently checked out by another process.</item>
/// </list>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Garbage-collect stale per-run worktrees under <c>polyphony-runs/</c>.
    /// </summary>
    /// <param name="apex">Optional apex root id to scope the scan to a single <c>apex-{N}/</c> subtree. When 0 (the default), the whole runs root is scanned.</param>
    /// <param name="commit">When true, actually remove the candidates via <c>git worktree remove --force</c>. When false (the default), only list candidates without mutation.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("gc")]
    [VerbResult(typeof(WorktreeGcResult))]
    public async Task<int> Gc(
        int apex = 0,
        bool commit = false,
        CancellationToken ct = default)
    {
        try
        {
            // Resolve runs root from the shared common-dir. We do NOT trust
            // the cwd here — gc must produce identical scans regardless of
            // which worktree it was invoked from.
            string? commonDir;
            try
            {
                commonDir = await _git.GetCommonDirAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitGc(commit, runsRoot: string.Empty, apex, [], 0, 0,
                    error: $"Could not resolve git common-dir: {ex.Message}");
                return ExitCodes.Success;
            }

            if (string.IsNullOrEmpty(commonDir))
            {
                EmitGc(commit, runsRoot: string.Empty, apex, [], 0, 0,
                    error: "Not inside a git repository (no common-dir resolvable).");
                return ExitCodes.Success;
            }

            string runsRoot;
            try
            {
                (runsRoot, _) = RunsRootResolver.Resolve(commonDir);
            }
            catch (ArgumentException ex)
            {
                EmitGc(commit, runsRoot: string.Empty, apex, [], 0, 0,
                    error: $"Could not resolve runs root from common-dir '{commonDir}': {ex.Message}");
                return ExitCodes.Success;
            }

            // List all worktrees and filter to those under runs_root (and,
            // when --apex is set, under apex-{N}/).
            var listResult = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!listResult.Succeeded)
            {
                var err = !string.IsNullOrWhiteSpace(listResult.Stderr)
                    ? listResult.Stderr.Trim()
                    : listResult.Stdout.Trim();
                EmitGc(commit, runsRoot, apex, [], 0, 0,
                    error: string.IsNullOrEmpty(err)
                        ? $"git worktree list exited with code {listResult.ExitCode}"
                        : err);
                return ExitCodes.Success;
            }

            IReadOnlyList<WorktreeEntry> entries;
            try
            {
                entries = ParsePorcelain(listResult.Stdout);
            }
            catch (FormatException ex)
            {
                EmitGc(commit, runsRoot, apex, [], 0, 0, error: ex.Message);
                return ExitCodes.Success;
            }

            var apexScopePath = apex > 0
                ? Path.GetFullPath(Path.Combine(runsRoot, $"apex-{apex.ToString(CultureInfo.InvariantCulture)}"))
                : null;

            var candidates = new List<WorktreeGcCandidate>();
            int removedCount = 0;
            int failedCount = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var entryPathCanonical = Path.GetFullPath(entry.Path).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!IsUnder(entryPathCanonical, runsRoot)) continue;
                if (apexScopePath is not null && !IsUnder(entryPathCanonical, apexScopePath)) continue;

                // Classify the prune reason. directory_missing wins over
                // branch_deleted (an entry with a missing dir is administrative
                // garbage regardless of branch state).
                string? reason = null;
                if (!Directory.Exists(entry.Path))
                {
                    reason = "directory_missing";
                }
                else if (!string.IsNullOrEmpty(entry.Branch))
                {
                    var sha = await _git.RevParseLocalBranchAsync(entry.Branch, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(sha))
                    {
                        reason = "branch_deleted";
                    }
                }

                if (reason is null) continue;

                if (!commit)
                {
                    candidates.Add(new WorktreeGcCandidate
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                        Reason = reason,
                        WouldRemove = true,
                        Removed = false,
                        Error = null,
                    });
                    removedCount++;
                    continue;
                }

                // Commit path: actually try to remove. --force lets us prune
                // even when git's metadata is half-broken (the directory is
                // gone or the branch is gone).
                var removeResult = await _git.WorktreeRemoveAsync(entry.Path, force: true, ct)
                    .ConfigureAwait(false);
                if (removeResult.Succeeded)
                {
                    candidates.Add(new WorktreeGcCandidate
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                        Reason = reason,
                        WouldRemove = false,
                        Removed = true,
                        Error = null,
                    });
                    removedCount++;
                }
                else
                {
                    var err = !string.IsNullOrWhiteSpace(removeResult.Stderr)
                        ? removeResult.Stderr.Trim()
                        : removeResult.Stdout.Trim();
                    candidates.Add(new WorktreeGcCandidate
                    {
                        Path = entry.Path,
                        Branch = entry.Branch,
                        Reason = reason,
                        WouldRemove = false,
                        Removed = false,
                        Error = string.IsNullOrEmpty(err)
                            ? $"git worktree remove exited with code {removeResult.ExitCode}"
                            : err,
                    });
                    failedCount++;
                }
            }

            EmitGc(commit, runsRoot, apex, candidates, removedCount, failedCount, error: null);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitGc(commit, runsRoot: string.Empty, apex, [], 0, 0, error: ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    /// <summary>
    /// Boundary-aware path containment check. Mirrors the same
    /// canonicalization the launcher and PathBoundary use: trailing
    /// separators are stripped, comparison uses OrdinalIgnoreCase on
    /// Windows / Ordinal elsewhere, and a path that equals the container
    /// counts as "under" (the container's own path is not a candidate
    /// because git only emits worktree entries for actual worktrees, but
    /// the equality case is harmless here).
    /// </summary>
    private static bool IsUnder(string candidate, string container)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var cand = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cont = container.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(cand, cont, cmp)) return true;
        return cand.StartsWith(cont + Path.DirectorySeparatorChar, cmp);
    }

    private static void EmitGc(
        bool commit,
        string runsRoot,
        int apex,
        IReadOnlyList<WorktreeGcCandidate> candidates,
        int removedCount,
        int failedCount,
        string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeGcResult
            {
                DryRun = !commit,
                RunsRoot = runsRoot,
                Apex = apex,
                Candidates = candidates,
                RemovedCount = removedCount,
                FailedCount = failedCount,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeGcResult));
    }
}
