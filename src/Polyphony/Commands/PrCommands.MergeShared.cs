using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
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
