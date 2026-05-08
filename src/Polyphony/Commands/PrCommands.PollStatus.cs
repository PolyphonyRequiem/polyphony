using System.Text.Json;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Poll a pull request's aggregated status and emit a platform-neutral
    /// snapshot. Used by every workflow that needs to route on
    /// <c>{approved, changes_requested, pending, merged, closed}</c> without
    /// caring whether the underlying source is GitHub or Azure DevOps.
    ///
    /// <para>GitHub is supported today; ADO is wired into the same schema
    /// in Phase 5. The verb also (optionally) parses plan-PR front-matter
    /// out of the PR body when <paramref name="includeMetadata"/> is set
    /// — task and MG PRs leave it null.</para>
    ///
    /// <para>Always exits 0 with a JSON payload describing the outcome
    /// (including errors). Routing-style verb — the consumer reads
    /// <c>state</c> + <c>policy.merge_allowed</c>.</para>
    /// </summary>
    /// <param name="prUrl">Full PR URL, e.g. <c>https://github.com/owner/repo/pull/123</c>.</param>
    /// <param name="includeMetadata">When true, parse <c>requests_parent_change</c> and <c>ancestor_plan_generations</c> from the PR body's YAML front-matter. Default false so the verb works on impl/MG PRs.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("poll-status")]
    [VerbResult(typeof(PrPollStatusResult))]
    public async Task<int> PollStatus(
        string prUrl,
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        if (!TryParsePrUrl(prUrl, out var slug, out var prNumber))
        {
            EmitPollError(prUrl, $"could not parse pr url '{prUrl}' (expected https://github.com/owner/repo/pull/N)");
            return ExitCodes.Success;
        }

        try
        {
            var data = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
            if (data is null)
            {
                EmitPollError(prUrl, $"PR #{prNumber} not found in {slug}", slug, prNumber);
                return ExitCodes.Success;
            }

            var state = MapState(data.State, data.ReviewDecision);
            var mergeable = MapMergeable(data.Mergeable);
            var reviewers = data.Reviews.Select(MapReviewer).ToList();
            var policy = ComputePolicy(state, mergeable);
            var metadata = includeMetadata ? PlanPrFrontMatter.Parse(data.Body) : null;

            var result = new PrPollStatusResult
            {
                PrUrl = prUrl,
                PrNumber = data.Number,
                RepoSlug = slug,
                State = state,
                HeadSha = data.HeadRefOid ?? string.Empty,
                HeadRef = data.HeadRefName ?? string.Empty,
                BaseRef = data.BaseRefName ?? string.Empty,
                Mergeable = mergeable,
                MergeCommitSha = data.MergeCommitSha,
                MergedAt = data.MergedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Reviewers = reviewers,
                Policy = policy,
                Metadata = metadata,
            };
            EmitPoll(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolTimeoutException ex)
        {
            EmitPollError(prUrl, $"gh pr view timed out after {ex.Attempts} attempt(s)", slug, prNumber);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitPollError(prUrl, ex.Message, slug, prNumber);
            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Parse a PR URL into <paramref name="repoSlug"/> + <paramref name="prNumber"/>.
    /// Accepts only the github.com hosted form; ADO URLs (which take a
    /// completely different shape) are routed through the ADO leg in
    /// Phase 5.
    /// </summary>
    private static bool TryParsePrUrl(string prUrl, out string repoSlug, out int prNumber)
    {
        repoSlug = string.Empty;
        prNumber = 0;
        if (string.IsNullOrWhiteSpace(prUrl)) return false;

        var match = Regex.Match(
            prUrl,
            @"^https?://github\.com/([^/]+/[^/]+)/pull/(\d+)(?:[/?#].*)?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[2].Value, out prNumber)) return false;
        repoSlug = match.Groups[1].Value;
        return true;
    }

    private static string MapState(string ghState, string reviewDecision)
    {
        // gh's state vocab: OPEN | CLOSED | MERGED. Closed-and-merged is "MERGED",
        // closed-without-merge is "CLOSED".
        if (string.Equals(ghState, "MERGED", StringComparison.OrdinalIgnoreCase)) return "merged";
        if (string.Equals(ghState, "CLOSED", StringComparison.OrdinalIgnoreCase)) return "closed";

        // Open — disambiguate via the aggregated review decision.
        if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)) return "changes_requested";
        return "pending";
    }

    private static bool? MapMergeable(string ghMergeable) => ghMergeable.ToUpperInvariant() switch
    {
        "MERGEABLE" => true,
        "CONFLICTING" => false,
        _ => null, // UNKNOWN, empty, anything else
    };

    private static PrPollReviewer MapReviewer(GhPullRequestReview review)
    {
        var vote = review.State.ToUpperInvariant() switch
        {
            "APPROVED" => "approved",
            "CHANGES_REQUESTED" => "changes_requested",
            "COMMENTED" => "commented",
            "DISMISSED" => "dismissed",
            "PENDING" => "pending",
            _ => review.State.ToLowerInvariant(),
        };
        return new PrPollReviewer
        {
            Identity = review.Login,
            Vote = vote,
            SubmittedAt = review.SubmittedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static PrPollPolicy ComputePolicy(string state, bool? mergeable)
    {
        var blockers = new List<string>();
        if (state != "approved") blockers.Add($"state is '{state}', not 'approved'");
        if (mergeable == false) blockers.Add("PR has merge conflicts");
        if (mergeable is null && state == "approved") blockers.Add("mergeable status not yet computed by GitHub");

        return new PrPollPolicy
        {
            MergeAllowed = blockers.Count == 0,
            BlockingReasons = blockers,
        };
    }

    private static void EmitPoll(PrPollStatusResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrPollStatusResult));

    private static void EmitPollError(
        string prUrl,
        string message,
        string repoSlug = "",
        int prNumber = 0)
    {
        var result = new PrPollStatusResult
        {
            PrUrl = prUrl,
            PrNumber = prNumber,
            RepoSlug = repoSlug,
            State = "error",
            HeadSha = string.Empty,
            HeadRef = string.Empty,
            BaseRef = string.Empty,
            Mergeable = null,
            Reviewers = [],
            Policy = new PrPollPolicy { MergeAllowed = false, BlockingReasons = [message] },
            Error = message,
        };
        EmitPoll(result);
    }
}
