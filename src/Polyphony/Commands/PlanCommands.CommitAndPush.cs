using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Postconditions;

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
///         a routing failure (<c>error_code: "git_failed"</c>, exit 0).</item>
///   <item>If the staging step left nothing to commit AND origin already
///         holds the requested paths at the on-disk content (verified by
///         <see cref="IPostconditionVerifier"/>), exit 0 with
///         <c>{ pushed: false, no_op_reason: "no_changes" }</c>. This is
///         the idempotent path on workflow resume.</item>
///   <item>If staging produced nothing but origin is missing one or more
///         paths (Class B bug fix matching the manifest verb's #192 guard),
///         push HEAD without committing — local HEAD already holds the
///         right blob.</item>
///   <item>Otherwise commit with <c>--message</c>, push to <c>origin/{branch}</c>,
///         emit <c>{ pushed: true, commit_sha, files_staged }</c>.</item>
/// </list>
///
/// <para>Routing-style: validation failures (Move #2 verb-layer pattern)
/// and git errors both emit a populated envelope and exit 0 — the
/// workflow routes on <c>error_code</c>, never on the OS exit code.</para>
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
    /// and push <paramref name="branch"/> to <c>origin</c>. Idempotent: a
    /// no-op when staging produces no changes AND origin already holds
    /// every requested path at the on-disk content.
    /// </summary>
    /// <param name="branch">Branch to commit on (e.g. <c>plan/100-101</c>).
    /// If the worktree is currently on a different branch, the verb checks
    /// it out first. Sentinel default <c>""</c> is caught in-body and
    /// surfaced as <c>invalid_inputs</c>.</param>
    /// <param name="message">Commit message. Required even on no-op runs
    /// (validated up front so the workflow can't omit it by accident).
    /// Sentinel default <c>""</c> is caught in-body.</param>
    /// <param name="paths">Comma-separated list of pathspecs to stage
    /// (e.g. <c>plans/plan-101.md,plans/plan-101-supporting.md</c>). Each
    /// entry is passed verbatim to <c>git add</c>. Sentinel default
    /// <c>""</c> is caught in-body.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("commit-and-push")]
    [VerbResult(typeof(PlanCommitAndPushResult))]
    public async Task<int> CommitAndPush(
        string branch = "",
        string message = "",
        string paths = "",
        CancellationToken ct = default)
    {
        // 1. Input validation. Routing-style envelope on missing inputs —
        //    the Move #2 verb-layer convention. Exit 0 throughout so the
        //    workflow routes on `error_code`, not on the OS exit code.
        if (string.IsNullOrWhiteSpace(branch))
        {
            EmitCommitAndPushError(branch, "invalid_inputs", "branch is required");
            return ExitCodes.Success;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            EmitCommitAndPushError(branch, "invalid_inputs", "message is required");
            return ExitCodes.Success;
        }
        var pathList = (paths ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathList.Length == 0)
        {
            EmitCommitAndPushError(branch, "invalid_inputs",
                "paths is required (one or more comma-separated pathspecs)");
            return ExitCodes.Success;
        }

        try
        {
            // 2. Make sure we're on the requested branch. The workflow should already
            //    be there (branch ensure-plan leaves the worktree on the plan branch),
            //    but checkout defensively for resume safety.
            var currentBranch = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            if (!string.Equals(currentBranch, branch, StringComparison.Ordinal))
            {
                await git.CheckoutAsync(branch, ct).ConfigureAwait(false);
            }

            // 3. Stage each requested path. `git add` is happy if the path is
            //    already up-to-date — it just doesn't add anything to the index.
            foreach (var pathspec in pathList)
            {
                await git.StageAsync(pathspec, ct).ConfigureAwait(false);
            }

            // 4. Decide whether anything is actually staged.
            //    `git status --porcelain` rows look like "XY filename" where
            //    column X is the index status and column Y is the worktree
            //    status. A non-space, non-? X column means staged-for-commit.
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            var stagedCount = status.Count(s => s.Length >= 2 && s[0] != ' ' && s[0] != '?');
            if (stagedCount == 0)
            {
                // 4a. Nothing staged → local HEAD already matches on-disk
                //     for every requested path. But that does NOT prove
                //     origin matches (Class B bug — same family as #192
                //     for the manifest verb). Defer to the shared
                //     IPostconditionVerifier so we don't silently mask
                //     "committed locally, never pushed" on workflow resume.
                var expectations = new List<PostconditionExpectation>(pathList.Length);
                foreach (var pathspec in pathList)
                {
                    // Best-effort read; if a pathspec resolves to a
                    // directory or otherwise isn't a single file, the
                    // verifier will see a missing/conflicting blob and
                    // route to NeedsPush, which is the safe answer.
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(pathspec, ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // File not readable as text (binary, dir, perms).
                        // Skip the comparison — verifier will treat as
                        // "missing" via the empty-string mismatch only if
                        // origin actually has empty content there, which
                        // is vanishingly rare. The push-anyway fallback
                        // below covers the case anyway.
                        content = string.Empty;
                    }
                    expectations.Add(new PostconditionExpectation(pathspec, content));
                }

                var outcome = await postconditions.VerifyAsync(
                    branch,
                    expectations,
                    ct: ct).ConfigureAwait(false);

                switch (outcome)
                {
                    case PostconditionOutcome.Satisfied:
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

                    case PostconditionOutcome.NeedsPush:
                    case PostconditionOutcome.Conflict:
                        // Push HEAD; no commit needed because HEAD already
                        // holds the right blob. Conflict (origin diverged)
                        // is treated the same as missing — let the push
                        // fail loudly with non-fast-forward, surfaced as
                        // `git_failed` by the catch block.
                        await git.PushAsync(branch, ct: ct).ConfigureAwait(false);
                        var headSha = await git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(
                            new PlanCommitAndPushResult
                            {
                                Branch = branch,
                                Pushed = true,
                                FilesStaged = 0,
                                CommitSha = headSha,
                            },
                            PolyphonyJsonContext.Default.PlanCommitAndPushResult));
                        return ExitCodes.Success;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled PostconditionOutcome: {outcome.GetType().Name}");
                }
            }

            // 5. Commit + push.
            await git.CommitAsync(message, ct).ConfigureAwait(false);
            await git.PushAsync(branch, ct: ct).ConfigureAwait(false);

            // 6. Resolve the HEAD SHA we just published so callers can record it.
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
            EmitCommitAndPushError(branch, "git_failed", ex.Message);
            return ExitCodes.Success;
        }
    }

    private static void EmitCommitAndPushError(string branch, string errorCode, string error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new PlanCommitAndPushResult
            {
                Branch = branch ?? string.Empty,
                Pushed = false,
                ErrorCode = errorCode,
                Error = error,
            },
            PolyphonyJsonContext.Default.PlanCommitAndPushResult));
    }
}
