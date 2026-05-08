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
        string prUrl = "",
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr poll-status",
            ("--pr-url", string.IsNullOrEmpty(prUrl))) is { } halt)
            return halt;

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

            // Build native-review reviewer list, then synthesize magic-comment
            // reviewer entries from the PR author's polyphony:approve /
            // polyphony:request-changes top-level comments. Per-identity
            // most-recent-wins aggregation across both sources runs in MapState.
            var nativeReviewers = data.Reviews.Select(MapReviewer).ToList();
            var magicReviewers = ExtractMagicCommentReviewers(data.AuthorLogin, data.Comments);
            var reviewers = nativeReviewers.Concat(magicReviewers).ToList();

            var state = MapState(data.State, data.ReviewDecision, data.AuthorLogin, reviewers);
            var mergeable = MapMergeable(data.Mergeable);
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
            EmitPollError(prUrl, ex.FormatErrorMessage("gh pr view"), slug, prNumber);
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

    /// <summary>
    /// Compose the platform-neutral state from gh's raw PR state, gh's
    /// aggregated <paramref name="reviewDecision"/>, and any magic-comment
    /// votes from the PR author.
    ///
    /// <para>Baseline: use gh's <paramref name="reviewDecision"/> (which
    /// already accounts for CODEOWNERS, dismissals, and the per-reviewer
    /// most-recent rule across native reviews).</para>
    ///
    /// <para>Override: when the PR author has posted a magic comment
    /// (<c>polyphony:approve</c> / <c>polyphony:request-changes</c>) AND
    /// that comment is more recent than any native APPROVED|CHANGES_REQUESTED
    /// review by the author, recompute by overlaying the author's vote on
    /// the native per-identity vote map. This is the only path to a vote
    /// for the PR author on GitHub (which blocks self-review).</para>
    ///
    /// <para>Cross-reviewer aggregation rule: any <c>changes_requested</c>
    /// blocks; otherwise any <c>approved</c> satisfies; otherwise
    /// <c>pending</c>. This matches GitHub's "branch protection requires
    /// approval" behavior at the level of granularity polyphony cares
    /// about (we don't model required-reviewers / CODEOWNERS independently).</para>
    /// </summary>
    private static string MapState(
        string ghState,
        string reviewDecision,
        string prAuthorLogin,
        IReadOnlyList<PrPollReviewer> reviewers)
    {
        // gh's state vocab: OPEN | CLOSED | MERGED. Closed-and-merged is "MERGED",
        // closed-without-merge is "CLOSED".
        if (string.Equals(ghState, "MERGED", StringComparison.OrdinalIgnoreCase)) return "merged";
        if (string.Equals(ghState, "CLOSED", StringComparison.OrdinalIgnoreCase)) return "closed";

        var magicVote = reviewers.FirstOrDefault(r =>
            r.Source == "magic_comment"
            && string.Equals(r.Identity, prAuthorLogin, StringComparison.OrdinalIgnoreCase));

        if (magicVote is null || string.IsNullOrEmpty(prAuthorLogin))
        {
            return MapStateFromReviewDecision(reviewDecision);
        }

        // Magic comment present — recompute via per-identity most-recent overlay.
        // Build per-identity most-recent vote map from native reviews only,
        // then overlay the author's magic vote if newer than their native review.
        var votesByReviewer = new Dictionary<string, (string Vote, DateTimeOffset At)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var r in reviewers)
        {
            if (r.Source == "magic_comment") continue;
            if (string.IsNullOrEmpty(r.Identity)) continue;
            if (r.Vote != "approved" && r.Vote != "changes_requested") continue;
            if (!TryParseRoundtrip(r.SubmittedAt, out var at)) continue;

            if (!votesByReviewer.TryGetValue(r.Identity, out var existing) || existing.At < at)
            {
                votesByReviewer[r.Identity] = (r.Vote, at);
            }
        }

        if (TryParseRoundtrip(magicVote.SubmittedAt, out var magicAt))
        {
            if (!votesByReviewer.TryGetValue(prAuthorLogin, out var existingAuthorVote)
                || existingAuthorVote.At < magicAt)
            {
                votesByReviewer[prAuthorLogin] = (magicVote.Vote, magicAt);
            }
        }

        if (votesByReviewer.Values.Any(v => v.Vote == "changes_requested")) return "changes_requested";
        if (votesByReviewer.Values.Any(v => v.Vote == "approved")) return "approved";
        return "pending";
    }

    private static string MapStateFromReviewDecision(string reviewDecision)
    {
        if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)) return "changes_requested";
        return "pending";
    }

    private static bool TryParseRoundtrip(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out result);

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
            Source = "review",
        };
    }

    /// <summary>
    /// Recognized magic-comment syntax (anchored to start of line, optional
    /// whitespace, case-insensitive). Trailing text on the same line is
    /// allowed (e.g. "polyphony:approve LGTM, ship it").
    ///
    /// <para>Restricted to PR-author comments (single-user mode) per issue #207.
    /// Comments from other identities are ignored — they should use native
    /// platform reviews. The author can't post a native review on their own
    /// PR on GitHub, so this is the only path to a synthetic vote for them.</para>
    /// </summary>
    private static readonly Regex MagicApproveRegex = new(
        @"^\s*polyphony:approve\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex MagicRequestChangesRegex = new(
        @"^\s*polyphony:request-changes\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Scan PR-author top-level comments for magic-comment votes. Returns
    /// at most one synthetic reviewer entry (the author's most recent magic
    /// comment, if any). Returns an empty list when:
    /// <list type="bullet">
    ///   <item>The PR author login is unknown (gh omitted the field).</item>
    ///   <item>No author comments contain a recognized magic-comment line.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Per-line matching: a comment containing
    /// <c>"polyphony:approve\nLGTM"</c> matches; a comment containing
    /// <c>"see polyphony:approve"</c> mid-line does NOT (anchored to line
    /// start). This avoids false positives in narrative discussion.
    /// </remarks>
    private static IReadOnlyList<PrPollReviewer> ExtractMagicCommentReviewers(
        string authorLogin,
        IReadOnlyList<GhPullRequestComment> comments)
    {
        if (string.IsNullOrEmpty(authorLogin) || comments.Count == 0)
            return Array.Empty<PrPollReviewer>();

        // Per author, track the most recent magic comment by createdAt.
        // For now restricted to PR author only (single-user mode); a future
        // change can extend this to a configurable set of identities.
        (string Vote, DateTimeOffset At)? mostRecent = null;
        foreach (var c in comments)
        {
            if (!string.Equals(c.AuthorLogin, authorLogin, StringComparison.OrdinalIgnoreCase))
                continue;
            if (c.CreatedAt is not { } at) continue;

            string? vote = null;
            // Most-recent line within a single comment wins by virtue of
            // last-match-wins below; cheaper to just check both regexes.
            if (MagicApproveRegex.IsMatch(c.Body)) vote = "approved";
            if (MagicRequestChangesRegex.IsMatch(c.Body)) vote = "changes_requested";
            if (vote is null) continue;

            if (mostRecent is null || mostRecent.Value.At < at)
            {
                mostRecent = (vote, at);
            }
        }

        if (mostRecent is null) return Array.Empty<PrPollReviewer>();

        return [
            new PrPollReviewer
            {
                Identity = authorLogin,
                Vote = mostRecent.Value.Vote,
                SubmittedAt = mostRecent.Value.At.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Source = "magic_comment",
            },
        ];
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
