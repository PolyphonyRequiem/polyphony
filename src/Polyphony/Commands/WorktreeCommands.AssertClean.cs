using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree assert-clean</c> — pre-flight gate for the launcher
/// and the apex driver. Asserts that the worktree at <c>--path</c> exists,
/// has no in-progress git operation, is clean (no <c>git status --porcelain</c>
/// entries), and (when <c>--expected-branch</c> is supplied) is checked out
/// to that branch.
///
/// <para>Routing-style verb: ALWAYS exits 0; consumers branch on
/// <c>output.ok</c>. The structured <c>output.reason</c> field
/// (<c>"path_missing"</c> | <c>"not_a_worktree"</c> | <c>"git_failed"</c> |
/// <c>"git_operation_in_progress"</c> | <c>"dirty"</c> |
/// <c>"wrong_branch"</c> | <c>"internal_error"</c> | <c>null</c>) lets the
/// workflow surface a precise human-gate prompt without re-running git.</para>
///
/// <para>This verb is the keystone of the AB#3085 hijack-prevention model:
/// the launcher and the driver both gate dispatch on <c>assert-clean</c>
/// succeeding for the target worktree. See the ADR at
/// <c>docs/decisions/per-run-worktree-model.md</c>.</para>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Stderr fragments that indicate the path is genuinely not a git
    /// worktree (vs. a transient or environmental git failure). Match
    /// case-insensitive substring; ordinal comparison.
    /// </summary>
    private static readonly string[] NotAWorktreeStderrFragments =
    [
        "not a git repository",
        "not a working tree",
        "is not a worktree",
    ];

    /// <summary>
    /// Assert that a worktree is safe to dispatch agents into.
    /// </summary>
    /// <param name="path">Worktree path to inspect. Defaults to the current directory when omitted.</param>
    /// <param name="expectedBranch">Optional branch the worktree is expected to be checked out to. When omitted, the branch check is skipped.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("assert-clean")]
    [VerbResult(typeof(WorktreeAssertCleanResult))]
    public async Task<int> AssertClean(
        string path = "",
        string expectedBranch = "",
        CancellationToken ct = default)
    {
        var resolvedPath = string.IsNullOrEmpty(path)
            ? Environment.CurrentDirectory
            : path;

        try
        {
            resolvedPath = System.IO.Path.GetFullPath(resolvedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException or PathTooLongException)
        {
            EmitAssert(false, resolvedPath, currentBranch: null, expectedBranch: NullIfEmpty(expectedBranch),
                reason: "path_missing", inProgress: null, dirtyPaths: [], error: ex.Message);
            return ExitCodes.Success;
        }

        if (!Directory.Exists(resolvedPath))
        {
            EmitAssert(false, resolvedPath, currentBranch: null, expectedBranch: NullIfEmpty(expectedBranch),
                reason: "path_missing", inProgress: null, dirtyPaths: [], error: null);
            return ExitCodes.Success;
        }

        IReadOnlyList<string> dirty;
        string? currentBranch;
        string? inProgress;
        try
        {
            dirty = await _git.GetStatusAsync(resolvedPath, ct).ConfigureAwait(false);
            currentBranch = NullIfEmpty(await _git.GetCurrentBranchAsync(resolvedPath, ct).ConfigureAwait(false));
            inProgress = await _git.GetInProgressOperationAsync(resolvedPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            // Discriminate "not a worktree" from "git itself failed" — the
            // operator's remediation is different for each. A locked index
            // or dubious-ownership refusal is NOT the same as a missing repo.
            var stderr = !string.IsNullOrWhiteSpace(ex.Stderr) ? ex.Stderr.Trim() : ex.Message;
            var reason = LooksLikeNotAWorktree(stderr) ? "not_a_worktree" : "git_failed";
            EmitAssert(false, resolvedPath, currentBranch: null, expectedBranch: NullIfEmpty(expectedBranch),
                reason: reason, inProgress: null, dirtyPaths: [], error: stderr);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            // Routing-style verb: ALWAYS exit 0 once we've decided to emit
            // an envelope — downstream shell scripts (launcher pre-flight,
            // driver pre-dispatch) parse stdout and a non-zero exit would
            // misroute them into "verb crashed, abort" before they ever
            // see the structured reason.
            EmitAssert(false, resolvedPath, currentBranch: null, expectedBranch: NullIfEmpty(expectedBranch),
                reason: "internal_error", inProgress: null, dirtyPaths: [], error: ex.Message);
            return ExitCodes.Success;
        }

        // In-progress operation fires BEFORE dirty: a paused rebase or merge
        // can leave both clean porcelain AND dirty index-state behind, and
        // the operator's first action is "abort or finish the operation",
        // not "stash my changes".
        if (inProgress is not null)
        {
            EmitAssert(false, resolvedPath, currentBranch: currentBranch, expectedBranch: NullIfEmpty(expectedBranch),
                reason: "git_operation_in_progress", inProgress: inProgress, dirtyPaths: dirty, error: null);
            return ExitCodes.Success;
        }

        // Dirty fires before wrong-branch — a dirty worktree on the wrong
        // branch is still unsafe to dispatch into, and the operator needs
        // the dirty-paths diagnostic before they can switch branches.
        if (dirty.Count > 0)
        {
            EmitAssert(false, resolvedPath, currentBranch: currentBranch, expectedBranch: NullIfEmpty(expectedBranch),
                reason: "dirty", inProgress: null, dirtyPaths: dirty, error: null);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrEmpty(expectedBranch) && !string.Equals(currentBranch, expectedBranch, StringComparison.Ordinal))
        {
            EmitAssert(false, resolvedPath, currentBranch: currentBranch, expectedBranch: expectedBranch,
                reason: "wrong_branch", inProgress: null, dirtyPaths: [], error: null);
            return ExitCodes.Success;
        }

        EmitAssert(true, resolvedPath, currentBranch: currentBranch, expectedBranch: NullIfEmpty(expectedBranch),
            reason: null, inProgress: null, dirtyPaths: [], error: null);
        return ExitCodes.Success;
    }

    private static bool LooksLikeNotAWorktree(string stderr)
    {
        foreach (var fragment in NotAWorktreeStderrFragments)
        {
            if (stderr.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static void EmitAssert(
        bool ok,
        string path,
        string? currentBranch,
        string? expectedBranch,
        string? reason,
        string? inProgress,
        IReadOnlyList<string> dirtyPaths,
        string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeAssertCleanResult
            {
                Ok = ok,
                Path = path,
                CurrentBranch = currentBranch,
                ExpectedBranch = expectedBranch,
                Reason = reason,
                InProgressOperation = inProgress,
                DirtyPaths = dirtyPaths,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeAssertCleanResult));
    }
}
