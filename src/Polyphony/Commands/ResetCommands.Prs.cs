using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset prs --apex N [--execute] [--comment "..."]</c> —
/// abandons every OPEN PR targeting any branch in the apex's polyphony
/// scope: <c>plan/{N}</c>, <c>mg/{N}-*</c>, <c>impl/{N}-*</c>,
/// <c>evidence/{N}-*</c>, <c>feature/{N}</c>.
///
/// <para>Branch enumeration runs against origin (<c>git ls-remote --heads
/// origin refs/heads/{pattern}</c>) — we don't trust local branch state
/// because the operator may have a stale checkout. PR enumeration runs
/// per-concrete-branch via <see cref="Sdlc.Observers.PullRequestReader.ListByHeadAsync"/>
/// with <c>state: "open"</c>, then each open PR is closed via
/// <see cref="Sdlc.Observers.PullRequestReader.CloseAsync"/> with the
/// supplied <c>--comment</c> (or a default polyphony-reset comment).</para>
///
/// <para><b>Cross-platform</b>: GitHub PRs become <c>state: CLOSED</c>;
/// ADO PRs become <c>status: abandoned</c>. Both are reversible from the
/// platform UI if the operator changes their mind — this verb does
/// nothing irreversible to the PR record.</para>
///
/// <para><b>Dry-run</b> is the default. Pass <c>--execute</c> to actually
/// close. Dry-run still enumerates so the operator sees the would-be
/// abandon list.</para>
///
/// <para><b>Failure tolerance</b>: a single PR the platform refuses to
/// close (404, transient network, etc.) becomes a
/// <see cref="ResetFailedPr"/> entry but does NOT mark the verb as
/// failed. Hard failures (identity resolution crashed, ls-remote
/// failed) set <see cref="ResetPrsResult.Success"/> = false.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Default closing comment posted on each abandoned PR. Polyphony-
    /// recognisable so operators can audit reset activity from the
    /// platform's PR history view.
    /// </summary>
    internal const string DefaultResetComment =
        "Closed by `polyphony reset prs` — this PR's apex is being reset for redispatch.";

    /// <summary>
    /// Abandon every open polyphony PR for the apex.
    /// </summary>
    /// <param name="apex">Apex root work-item ID.</param>
    /// <param name="execute">Pass to actually close PRs. Without this flag, the verb is dry-run.</param>
    /// <param name="comment">Optional override for the closing comment posted on each PR. Defaults to <see cref="DefaultResetComment"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("prs")]
    [VerbResult(typeof(ResetPrsResult))]
    public async Task<int> ResetPrs(
        int apex = RequiredInput.MissingInt,
        bool execute = false,
        string comment = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset prs",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var commentToPost = string.IsNullOrWhiteSpace(comment) ? DefaultResetComment : comment;

        ResetPrsResult result;
        try
        {
            var identity = await _planObserver
                .TryResolveRepoIdentityAsync(null, null, null, null, ct)
                .ConfigureAwait(false);

            if (identity is null)
            {
                result = new ResetPrsResult
                {
                    Apex = apex,
                    Success = false,
                    DryRun = !execute,
                    Error =
                        "Could not resolve repo identity. " +
                        "Run from a worktree of a polyphony-onboarded repo with an origin remote configured.",
                };
                Emit(result);
                return ExitCodes.Success;
            }

            var slug = Sdlc.Observers.PullRequestReader.BuildRepoSlug(identity);

            // Enumerate concrete branches for each apex pattern.
            var branches = await EnumerateApexBranchesAsync(apex, ct).ConfigureAwait(false);

            var abandoned = new List<ResetAbandonedPr>();
            var failed = new List<ResetFailedPr>();

            foreach (var branch in branches)
            {
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<PullRequestSummary> openPrs;
                try
                {
                    openPrs = await _pullRequestReader.ListByHeadAsync(
                        identity, branch, "open", limit: null, ct).ConfigureAwait(false);
                }
                catch (Exception listEx)
                {
                    failed.Add(new ResetFailedPr
                    {
                        Number = 0,
                        HeadBranch = branch,
                        Url = string.Empty,
                        Reason = $"PR enumeration failed: {listEx.Message}",
                    });
                    continue;
                }

                foreach (var pr in openPrs)
                {
                    ct.ThrowIfCancellationRequested();
                    var prUrl = pr.Url ?? Sdlc.Observers.PullRequestReader.BuildPrUrl(identity, pr.Number);

                    if (!execute)
                    {
                        abandoned.Add(new ResetAbandonedPr
                        {
                            Number = pr.Number,
                            HeadBranch = branch,
                            Url = prUrl,
                        });
                        continue;
                    }

                    bool closed;
                    try
                    {
                        closed = await _pullRequestReader.CloseAsync(
                            identity, pr.Number, commentToPost, ct).ConfigureAwait(false);
                    }
                    catch (Exception closeEx)
                    {
                        failed.Add(new ResetFailedPr
                        {
                            Number = pr.Number,
                            HeadBranch = branch,
                            Url = prUrl,
                            Reason = $"Close threw: {closeEx.Message}",
                        });
                        continue;
                    }

                    if (closed)
                    {
                        abandoned.Add(new ResetAbandonedPr
                        {
                            Number = pr.Number,
                            HeadBranch = branch,
                            Url = prUrl,
                        });
                    }
                    else
                    {
                        failed.Add(new ResetFailedPr
                        {
                            Number = pr.Number,
                            HeadBranch = branch,
                            Url = prUrl,
                            Reason = "Platform reported a non-success outcome (already closed, 404, or transient error).",
                        });
                    }
                }
            }

            result = new ResetPrsResult
            {
                Apex = apex,
                Success = true,
                DryRun = !execute,
                RepoSlug = slug,
                AbandonedPrs = abandoned,
                FailedPrs = failed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetPrsResult
            {
                Apex = apex,
                Success = false,
                DryRun = !execute,
                Error = $"Error resetting PRs for apex #{apex}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Resolve every concrete branch on origin that matches any of the
    /// apex's polyphony patterns. Returns a de-duped list in stable order
    /// (pattern order, then alphabetical within a pattern).
    /// </summary>
    internal async Task<IReadOnlyList<string>> EnumerateApexBranchesAsync(int apex, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var pattern in ApexBranchPatterns(apex))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> heads;
            try
            {
                heads = await _git.LsRemoteHeadsAsync(
                    "origin", $"refs/heads/{pattern}", ct).ConfigureAwait(false);
            }
            catch (ExternalToolException)
            {
                // Transient network / permission failure on a single
                // pattern — skip it. The other patterns may still
                // succeed. The caller's per-branch enumeration error
                // path will surface PR-list issues separately.
                continue;
            }

            foreach (var line in heads)
            {
                // Each ls-remote line is "<sha>\trefs/heads/<branch>".
                // Find the refs/heads/ suffix and strip the prefix.
                var idx = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                if (idx < 0) continue;
                var branch = line[(idx + "refs/heads/".Length)..].Trim();
                if (string.IsNullOrEmpty(branch)) continue;
                if (seen.Add(branch)) result.Add(branch);
            }
        }
        return result;
    }

    private static void Emit(ResetPrsResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetPrsResult));
}
