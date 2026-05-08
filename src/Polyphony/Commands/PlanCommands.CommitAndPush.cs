using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan commit-and-push</c> — stages the supplied paths on the
/// supplied branch, creates a commit (when staging produced anything to
/// commit), and pushes to <c>origin</c>.
///
/// <para>Verb contract:</para>
/// <list type="bullet">
///   <item>If the worktree is on a different branch, <c>git checkout</c> first.</item>
///   <item>Stage every <c>--paths</c> entry. Paths that don't exist are
///         a hard failure (<c>RoutingFailure</c>).</item>
///   <item>If the staging step left nothing to commit (the supplied paths
///         already match HEAD), exit 0 with
///         <c>{ pushed: false, no_op_reason: "no_changes" }</c>. This is the
///         idempotent path on workflow resume.</item>
///   <item>Otherwise commit with <c>--message</c>, push to <c>origin/{branch}</c>,
///         emit <c>{ pushed: true, commit_sha, files_staged }</c>.</item>
/// </list>
///
/// <para>This is the transport primitive consumed by <c>plan-level.yaml</c>
/// after the architect writes a plan file. It is intentionally generic —
/// the same verb commits a root plan, a child plan, or a revised plan; the
/// caller supplies the branch name and the paths.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Stage <paramref name="paths"/>, commit with <paramref name="message"/>,
    /// and push <paramref name="branch"/> to <c>origin</c>. Idempotent: a no-op
    /// when staging produces no changes.
    /// </summary>
    /// <param name="branch">Branch to commit on (e.g. <c>plan/100-101</c>).
    /// If the worktree is currently on a different branch, the verb checks
    /// it out first.</param>
    /// <param name="message">Commit message. Required even on no-op runs
    /// (validated up front so the workflow can't omit it by accident).</param>
    /// <param name="paths">Comma-separated list of pathspecs to stage
    /// (e.g. <c>plans/plan-101.md,plans/plan-101-supporting.md</c>). Each
    /// entry is passed verbatim to <c>git add</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("commit-and-push")]
    [VerbResult(typeof(PlanCommitAndPushResult))]
    public async Task<int> CommitAndPush(
        string branch,
        string message,
        string paths,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            EmitCommitAndPushError(branch, "branch is required");
            return ExitCodes.ConfigError;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            EmitCommitAndPushError(branch, "message is required");
            return ExitCodes.ConfigError;
        }
        var pathList = (paths ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathList.Length == 0)
        {
            EmitCommitAndPushError(branch, "paths is required (one or more comma-separated pathspecs)");
            return ExitCodes.ConfigError;
        }

        try
        {
            // 1. Make sure we're on the requested branch. The workflow should already
            //    be there (branch ensure-plan leaves the worktree on the plan branch),
            //    but checkout defensively for resume safety.
            var currentBranch = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            if (!string.Equals(currentBranch, branch, StringComparison.Ordinal))
            {
                await git.CheckoutAsync(branch, ct).ConfigureAwait(false);
            }

            // 2. Stage each requested path. `git add` is happy if the path is
            //    already up-to-date — it just doesn't add anything to the index.
            foreach (var pathspec in pathList)
            {
                await git.StageAsync(pathspec, ct).ConfigureAwait(false);
            }

            // 3. Decide whether anything is actually staged.
            //    `git status --porcelain` rows look like "XY filename" where
            //    column X is the index status and column Y is the worktree
            //    status. A non-space, non-? X column means staged-for-commit.
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            var stagedCount = status.Count(s => s.Length >= 2 && s[0] != ' ' && s[0] != '?');
            if (stagedCount == 0)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new PlanCommitAndPushResult
                    {
                        Branch = branch,
                        Pushed = false,
                        FilesStaged = 0,
                        NoOpReason = "no_changes",
                    },
                    PolyphonyJsonContext.Default.PlanCommitAndPushResult));
                return ExitCodes.Success;
            }

            // 4. Commit + push.
            await git.CommitAsync(message, ct).ConfigureAwait(false);
            await git.PushAsync(branch, ct: ct).ConfigureAwait(false);

            // 5. Resolve the HEAD SHA we just published so callers can record it.
            //    RevParseLocalBranchAsync returns null only if the branch vanished;
            //    we just committed to it, so this should always resolve.
            var sha = await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(
                new PlanCommitAndPushResult
                {
                    Branch = branch,
                    Pushed = true,
                    FilesStaged = stagedCount,
                    CommitSha = sha,
                },
                PolyphonyJsonContext.Default.PlanCommitAndPushResult));
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            EmitCommitAndPushError(branch, ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitCommitAndPushError(string branch, string error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new PlanCommitAndPushResult
            {
                Branch = branch ?? string.Empty,
                Pushed = false,
                Error = error,
            },
            PolyphonyJsonContext.Default.PlanCommitAndPushResult));
    }
}
