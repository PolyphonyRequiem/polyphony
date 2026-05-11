using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Worktrees;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree init-apex --apex N</c> — bootstrap the per-apex
/// worktree tree under <c>{runs_root}/apex-{N}/</c> with the
/// <c>feature/{N}</c> branch checked out at
/// <c>{runs_root}/apex-{N}/feature-{N}/</c>.
///
/// <para><b>What it does (in order):</b></para>
/// <list type="number">
///   <item>Resolve <c>(runs_root, main_worktree_path)</c> from
///         <c>git rev-parse --git-common-dir</c> via
///         <see cref="RunsRootResolver"/>.</item>
///   <item>Compute <c>apex_root = {runs_root}/apex-{N}</c>,
///         <c>worktree_path = {apex_root}/feature-{N}</c>,
///         <c>branch = feature/{N}</c>.</item>
///   <item>Defensively assert <c>worktree_path</c> is under
///         <c>runs_root</c> AND not under <c>main_worktree_path</c>
///         (boundary-aware via <see cref="PathBoundary.IsSameOrSubpath"/>).</item>
///   <item><c>Directory.CreateDirectory(apex_root)</c>.</item>
///   <item>Run the create-or-attach matrix (path-exists wins over
///         branch-state, as required by the AB#3085 design).</item>
/// </list>
///
/// <para><b>Why this verb does NOT refuse cwd-inside-main:</b> the
/// launcher (<c>scripts/Invoke-PolyphonySdlc.ps1</c>) MUST run
/// <c>init-apex</c> from the main worktree during apex bootstrap; the
/// hijack invariant is preserved by self-derivation of
/// <c>worktree_path</c> from the canonicalized common-dir, NOT by
/// cwd refusal. The sister verb <c>worktree create</c> (PR 1b3) keeps
/// cwd-refusal as a belt-and-suspenders guardrail because nothing
/// legitimate calls it from the main worktree.</para>
///
/// <para>Always exits 0 (routing-style verb); consumers branch on
/// <c>Outcome</c> + <c>Reason</c>.</para>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Initialise the per-apex worktree tree.
    /// </summary>
    /// <param name="apex">Apex root work-item id (positive integer).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("init-apex")]
    [VerbResult(typeof(WorktreeInitApexResult))]
    public async Task<int> InitApex(
        int apex = RequiredInput.MissingInt,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("worktree init-apex",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (apex <= 0)
        {
            EmitInitApex(
                apex, apexRoot: null, worktreePath: null, branch: null,
                outcome: "failed", reason: "invalid_apex",
                error: $"--apex must be positive (got {apex}).");
            return ExitCodes.Success;
        }

        // ── Step 1-2: resolve (runs_root, main_path) and derive paths ──
        string commonDir;
        try
        {
            var raw = await _git.GetCommonDirAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                EmitInitApex(
                    apex, apexRoot: null, worktreePath: null, branch: null,
                    outcome: "failed", reason: "common_dir_unavailable",
                    error: "git rev-parse --git-common-dir returned no path; cwd is not inside a git repository.");
                return ExitCodes.Success;
            }
            commonDir = raw;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitInitApex(
                apex, apexRoot: null, worktreePath: null, branch: null,
                outcome: "failed", reason: "common_dir_unavailable",
                error: ex.Message);
            return ExitCodes.Success;
        }

        string runsRoot, mainPath;
        try
        {
            (runsRoot, mainPath) = RunsRootResolver.Resolve(commonDir);
        }
        catch (ArgumentException ex)
        {
            EmitInitApex(
                apex, apexRoot: null, worktreePath: null, branch: null,
                outcome: "failed", reason: "common_dir_unavailable",
                error: $"Could not derive runs-root from common-dir '{commonDir}': {ex.Message}");
            return ExitCodes.Success;
        }

        var apexRoot = Path.Combine(runsRoot, $"apex-{apex}");
        var worktreePath = Path.Combine(apexRoot, $"feature-{apex}");
        var branch = $"feature/{apex}";

        // ── Step 3: defensive boundary invariant ──
        // RunsRootResolver guarantees runs_root is sibling-of-main, so
        // these checks SHOULD always hold — but a misconfigured layout
        // (junctions, weird basenames) could break the invariant, and
        // surfacing that as a routing error beats silently corrupting
        // the main worktree.
        if (!PathBoundary.IsSameOrSubpath(runsRoot, worktreePath))
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "filesystem_failure",
                error: $"Derived worktree path '{worktreePath}' is not inside runs-root '{runsRoot}'. Refusing.");
            return ExitCodes.Success;
        }
        if (PathBoundary.IsSameOrSubpath(mainPath, worktreePath))
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "filesystem_failure",
                error: $"Derived worktree path '{worktreePath}' is inside the main worktree '{mainPath}'. Refusing to hijack.");
            return ExitCodes.Success;
        }

        // ── Step 4: ensure the apex-root container exists ──
        try
        {
            Directory.CreateDirectory(apexRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException or PathTooLongException)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "filesystem_failure",
                error: $"Could not create apex root '{apexRoot}': {ex.Message}");
            return ExitCodes.Success;
        }

        // ── Step 5: matrix ──
        return await RunInitApexMatrixAsync(
            apex, apexRoot, worktreePath, branch, ct).ConfigureAwait(false);
    }

    private async Task<int> RunInitApexMatrixAsync(
        int apex,
        string apexRoot,
        string worktreePath,
        string branch,
        CancellationToken ct)
    {
        // 1. List existing worktrees (single source of truth for both
        //    "is target path already a worktree" and "is branch already
        //    checked out elsewhere").
        var listResult = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
        if (!listResult.Succeeded)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "git_failure",
                error: GitStderrOrFallback(listResult, "git worktree list"));
            return ExitCodes.Success;
        }

        IReadOnlyList<WorktreeEntry> entries;
        try
        {
            entries = ParsePorcelain(listResult.Stdout);
        }
        catch (FormatException ex)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "git_failure",
                error: $"Could not parse 'git worktree list --porcelain' output: {ex.Message}");
            return ExitCodes.Success;
        }

        // 2. Path-exists wins over branch-state.
        var existing = FindByPath(entries, worktreePath);
        if (existing is not null)
        {
            if (string.Equals(existing.Branch, branch, StringComparison.Ordinal))
            {
                EmitInitApex(
                    apex, apexRoot, worktreePath, branch,
                    outcome: "idempotent", reason: null, error: null);
                return ExitCodes.Success;
            }

            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "path_exists_wrong_branch",
                error: $"Worktree at '{worktreePath}' is on branch '{existing.Branch ?? "(detached)"}', not '{branch}'.");
            return ExitCodes.Success;
        }

        if (PathExists(worktreePath))
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "path_exists_not_worktree",
                error: $"Path '{worktreePath}' exists but is not a registered git worktree.");
            return ExitCodes.Success;
        }

        // 3. Branch existence + remote-branch asymmetry.
        string? branchSha;
        try
        {
            branchSha = await _git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "git_failure",
                error: $"git rev-parse --verify refs/heads/{branch} failed: {ex.Message}");
            return ExitCodes.Success;
        }

        if (branchSha is null)
        {
            // Local branch missing — but is it on origin? Refuse to fork
            // from main when the apex branch already lives on the remote.
            IReadOnlyList<string> remotes;
            try
            {
                remotes = await _git.ListRemoteBranchesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitInitApex(
                    apex, apexRoot, worktreePath, branch,
                    outcome: "failed", reason: "git_failure",
                    error: $"git branch -r failed: {ex.Message}");
                return ExitCodes.Success;
            }

            if (remotes.Any(r => string.Equals(r, branch, StringComparison.Ordinal)))
            {
                EmitInitApex(
                    apex, apexRoot, worktreePath, branch,
                    outcome: "failed", reason: "remote_branch_exists",
                    error: $"Local branch '{branch}' is missing but origin/{branch} exists. " +
                           "init-apex is local-only and refuses to fork; fetch and create the local branch first.");
                return ExitCodes.Success;
            }

            return await CreateThenMaybeIdempotentAsync(
                apex, apexRoot, worktreePath, branch, ct).ConfigureAwait(false);
        }

        // Local branch exists. Is it already checked out elsewhere?
        var holder = entries.FirstOrDefault(e =>
            e.Branch is not null && string.Equals(e.Branch, branch, StringComparison.Ordinal));
        if (holder is not null)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "failed", reason: "branch_in_use",
                error: $"Branch '{branch}' is already checked out at '{holder.Path}'.");
            return ExitCodes.Success;
        }

        return await AttachThenMaybeIdempotentAsync(
            apex, apexRoot, worktreePath, branch, ct).ConfigureAwait(false);
    }

    private async Task<int> CreateThenMaybeIdempotentAsync(
        int apex, string apexRoot, string worktreePath, string branch, CancellationToken ct)
    {
        var addResult = await _git.WorktreeAddAsync(branch, worktreePath, "main", ct).ConfigureAwait(false);
        if (addResult.Succeeded)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "created", reason: null, error: null);
            return ExitCodes.Success;
        }

        // Race tolerance: another process may have created the worktree
        // between our list call and ours. Re-list and re-classify.
        if (await ProbeIdempotentAsync(worktreePath, branch, ct).ConfigureAwait(false))
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "idempotent", reason: null, error: null);
            return ExitCodes.Success;
        }

        EmitInitApex(
            apex, apexRoot, worktreePath, branch,
            outcome: "failed", reason: "git_failure",
            error: GitStderrOrFallback(addResult, $"git worktree add -b {branch}"));
        return ExitCodes.Success;
    }

    private async Task<int> AttachThenMaybeIdempotentAsync(
        int apex, string apexRoot, string worktreePath, string branch, CancellationToken ct)
    {
        var attachResult = await _git.WorktreeAddAttachAsync(branch, worktreePath, ct).ConfigureAwait(false);
        if (attachResult.Succeeded)
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "attached", reason: null, error: null);
            return ExitCodes.Success;
        }

        if (await ProbeIdempotentAsync(worktreePath, branch, ct).ConfigureAwait(false))
        {
            EmitInitApex(
                apex, apexRoot, worktreePath, branch,
                outcome: "idempotent", reason: null, error: null);
            return ExitCodes.Success;
        }

        EmitInitApex(
            apex, apexRoot, worktreePath, branch,
            outcome: "failed", reason: "git_failure",
            error: GitStderrOrFallback(attachResult, $"git worktree add {worktreePath} {branch}"));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Re-list worktrees and check whether <paramref name="worktreePath"/>
    /// is now a worktree on <paramref name="branch"/>. Used to recover
    /// from concurrent-creation races. Swallows list/parse failures
    /// (returns false) so the original git error remains the reported
    /// diagnostic.
    /// </summary>
    private async Task<bool> ProbeIdempotentAsync(string worktreePath, string branch, CancellationToken ct)
    {
        try
        {
            var probe = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!probe.Succeeded) return false;
            var entries = ParsePorcelain(probe.Stdout);
            var entry = FindByPath(entries, worktreePath);
            return entry is not null
                && string.Equals(entry.Branch, branch, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }

    private static WorktreeEntry? FindByPath(IReadOnlyList<WorktreeEntry> entries, string worktreePath)
    {
        var target = NormalizePath(worktreePath);
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var entry in entries)
        {
            if (string.Equals(NormalizePath(entry.Path), target, cmp)) return entry;
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool PathExists(string path)
        => Directory.Exists(path) || File.Exists(path);

    private static string GitStderrOrFallback(Polyphony.Infrastructure.Processes.ProcessResult result, string verbDescription)
    {
        var err = !string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stderr.Trim()
            : result.Stdout.Trim();
        return string.IsNullOrEmpty(err)
            ? $"{verbDescription} exited with code {result.ExitCode}"
            : err;
    }

    private static void EmitInitApex(
        int apex,
        string? apexRoot,
        string? worktreePath,
        string? branch,
        string outcome,
        string? reason,
        string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeInitApexResult
            {
                ApexId = apex,
                ApexRoot = apexRoot,
                WorktreePath = worktreePath,
                Branch = branch,
                Outcome = outcome,
                Reason = reason,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeInitApexResult));
    }
}
