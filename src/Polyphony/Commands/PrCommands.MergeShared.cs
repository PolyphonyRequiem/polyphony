using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Outcome of <see cref="ValidateCompletedAdoPrAsync"/>.
    /// <see cref="IsValid"/> is <c>true</c> when the completed PR's
    /// recorded source SHA is the same commit that currently sits on
    /// <c>origin/{headBranch}</c> (clause 1), OR the head branch is gone
    /// from origin and the merge commit is reachable from
    /// <c>origin/{baseBranch}</c> (clause 2). On <c>true</c>,
    /// <see cref="MergeCommit"/> carries the merge SHA the verb should
    /// emit; on <c>false</c> the PR is a stale audit artifact from a
    /// prior run and must be ignored.
    /// </summary>
    /// <remarks>
    /// Empty/null SHA fields are treated conservatively as "cannot
    /// confirm" — they fail the validity check rather than passing it.
    /// </remarks>
    internal readonly record struct CompletedPrValidity(bool IsValid, string? MergeCommit);

    /// <summary>
    /// Decide whether a "completed" ADO PR matched by branch-name pair
    /// still represents the current state of the working branches, or
    /// whether it is a stale record left over from a previous workflow
    /// run that recycled the same branch names.
    ///
    /// <para>The bug this guards against: ADO never deletes PR records.
    /// A reset that wipes branches and a redispatch that recreates them
    /// with the same canonical names will surface yesterday's completed
    /// PR via list-by-source-branch — verbs that trust the match return
    /// <c>already_merged: true</c> with yesterday's merge SHA, and the
    /// real implementation work is silently lost (caught by
    /// AB#3211 squash-coverage assertion, but only as a gate the
    /// operator must resolve).</para>
    ///
    /// <para>Validity rule (both clauses are sufficient on their own):
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       The PR's recorded <c>lastMergeSourceCommit</c> equals the
    ///       SHA at <c>origin/{headBranch}</c> right now. Branch survived
    ///       merge (e.g. <c>--delete-branch false</c>), no recycling
    ///       happened, retry-before-deletion is safe to short-circuit.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>origin/{headBranch}</c> does not exist AND the PR's
    ///       merge commit is reachable from <c>origin/{baseBranch}</c>.
    ///       Branch was deleted on PR completion (the common case) and
    ///       the merge actually landed on the integration trunk —
    ///       retry-after-deletion is still safe to short-circuit.
    ///       Note: this clause is permissive for non-squash merges where
    ///       the merge commit's ancestry includes the PR's work; for
    ///       squash merges the merge commit IS the squash commit on base
    ///       so ancestry still holds.
    ///     </description>
    ///   </item>
    /// </list>
    /// Any other state (head exists but at a different SHA; head exists
    /// but PR poll-data is unreadable; merge commit missing) is treated
    /// as <c>IsValid=false</c>. The caller falls back to its
    /// "no matching completed PR" path, which typically opens a fresh
    /// active PR.
    /// </para>
    ///
    /// <para>One ADO round-trip (<c>GetPullRequestPollDataAsync</c>) and
    /// one local git ls-remote per call. Acceptable: the path runs at
    /// most once per verb invocation on the optimistic completed-match.</para>
    /// </summary>
    internal async Task<CompletedPrValidity> ValidateCompletedAdoPrAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string headBranch,
        string baseBranch,
        CancellationToken ct)
    {
        if (ado is null) return new CompletedPrValidity(false, null);

        AdoPullRequestPollData? poll;
        try
        {
            poll = await ado.GetPullRequestPollDataAsync(
                organization, project, repository, pullRequestId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Conservative: unreadable poll-data means we cannot confirm
            // the PR is current; treat as stale so the caller proceeds
            // to create a fresh active PR.
            return new CompletedPrValidity(false, null);
        }

        if (poll is null) return new CompletedPrValidity(false, null);

        var prSourceSha = poll.HeadRefOid;
        var mergeCommit = poll.MergeCommit;

        var originHeadSha = await ResolveOriginBranchShaAsync(headBranch, ct).ConfigureAwait(false);

        // Clause 1: head exists on origin and matches the PR's recorded source.
        if (!string.IsNullOrEmpty(originHeadSha)
            && !string.IsNullOrEmpty(prSourceSha)
            && string.Equals(originHeadSha, prSourceSha, StringComparison.OrdinalIgnoreCase))
        {
            return new CompletedPrValidity(true, mergeCommit);
        }

        // Clause 2: head is gone, but the PR's merge commit reached base.
        if (string.IsNullOrEmpty(originHeadSha) && !string.IsNullOrEmpty(mergeCommit))
        {
            bool reachable;
            try
            {
                reachable = await git.IsAncestorAsync(
                    mergeCommit, $"origin/{baseBranch}", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                reachable = false;
            }

            if (reachable) return new CompletedPrValidity(true, mergeCommit);
        }

        return new CompletedPrValidity(false, null);
    }

    /// <summary>
    /// Resolve the current SHA at <c>origin/{branch}</c> via
    /// <c>git ls-remote --heads origin refs/heads/{branch}</c>. Returns
    /// the SHA, or <c>null</c> when the branch does not exist on origin
    /// (or when the ls-remote call fails — both surface as "missing"
    /// from the caller's perspective).
    /// </summary>
    private async Task<string?> ResolveOriginBranchShaAsync(string branch, CancellationToken ct)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{branch}", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        if (lines.Count == 0) return null;

        // ls-remote output: "{sha}\trefs/heads/{name}"
        var parts = lines[0].Split('\t', 2);
        if (parts.Length == 0) return null;
        var sha = parts[0].Trim();
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    /// <summary>
    /// Resolution of the (head, base) -> PR lookup used by both
    /// <c>merge-impl-pr</c> and <c>merge-mg-pr</c>. Exactly one of
    /// <see cref="OpenPr"/>, <see cref="AlreadyMergedPr"/>, or
    /// <see cref="Error"/> is populated. The "already merged" path makes
    /// the verbs idempotent — a workflow that loses the response of a
    /// successful first call can re-issue the verb and observe success.
    /// </summary>
    /// <param name="OpenPr">The open PR matching (head, base), if any.</param>
    /// <param name="AlreadyMergedPr">The most recent merged PR matching (head, base), if no open PR exists.</param>
    /// <param name="Error">Diagnostic error when no matching PR (open or merged) was found, or when slug resolution failed.</param>
    private readonly record struct MergePrResolution(
        PullRequestSummary? OpenPr,
        PullRequestSummary? AlreadyMergedPr,
        string? Error);

    /// <summary>
    /// Look up the PR matching (head, base). First tries open PRs (we are
    /// here to merge an open one). If none, falls back to the most recent
    /// merged PR for that pair so the verb can report
    /// <c>already_merged: true</c> on retry. Returns an error when neither
    /// exists.
    /// </summary>
    private async Task<MergePrResolution> FindPrForMergeAsync(
        string repoSlug,
        string headBranch,
        string baseBranch,
        CancellationToken ct)
    {
        var openMatches = await gh.ListPullRequestsAsync(
            repoSlug,
            new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
            ct).ConfigureAwait(false);
        if (openMatches.Count > 0)
        {
            return new MergePrResolution(openMatches[0], null, null);
        }

        var mergedMatches = await gh.ListPullRequestsAsync(
            repoSlug,
            new PrListFilters(Head: headBranch, Base: baseBranch, State: "merged", Limit: 1),
            ct).ConfigureAwait(false);
        if (mergedMatches.Count > 0)
        {
            return new MergePrResolution(null, mergedMatches[0], null);
        }

        return new MergePrResolution(
            null,
            null,
            $"no pull request found for head '{headBranch}' -> base '{baseBranch}'");
    }

    /// <summary>
    /// Parse the operator-supplied <c>--method</c> flag. Accepts
    /// <c>squash</c>, <c>merge</c>, <c>rebase</c> case-insensitively.
    /// </summary>
    private static bool TryParseMethod(string raw, out GhMergeMethod method, out string error)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "squash":
                method = GhMergeMethod.Squash;
                error = "";
                return true;
            case "merge":
            case "merge-commit":
                method = GhMergeMethod.Merge;
                error = "";
                return true;
            case "rebase":
                method = GhMergeMethod.Rebase;
                error = "";
                return true;
            default:
                method = GhMergeMethod.Squash;
                error = $"merge method must be 'squash', 'merge', or 'rebase' (got '{raw}')";
                return false;
        }
    }
}
