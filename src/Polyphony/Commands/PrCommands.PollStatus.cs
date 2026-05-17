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

            // Build review-thread snapshot. Threads are now the source
            // of truth for the changes_requested gate (issue #207).
            // Failure to fetch threads is non-fatal — the verb still
            // emits a result envelope, but appends a warning so consumers
            // know the threads field may not reflect reality.
            List<PrPollThread> threads = [];
            List<string>? warnings = null;
            var threadsRead = await gh.GetPullRequestReviewThreadsAsync(slug, prNumber, ct).ConfigureAwait(false);
            if (threadsRead is null)
            {
                (warnings ??= []).Add(
                    $"Could not fetch review threads for PR #{prNumber} (gh api graphql failed). " +
                    "Status field reflects native review decision only — unresolved review threads were not considered.");
            }
            else
            {
                threads = threadsRead.Threads.Select(MapGhThread).ToList();
                if (threadsRead.HasMorePages)
                {
                    (warnings ??= []).Add(PrPollStateDerivation.ThreadPaginationWarning);
                }
            }

            // Build native-review reviewer list. Magic-comment-as-review
            // synthesis is retired — those comments no longer influence
            // routing. Their presence is surfaced as a deprecation
            // warning so operators with muscle memory realize the
            // workaround is a silent no-op.
            var reviewers = data.Reviews.Select(MapReviewer).ToList();
            var state = PrPollStateDerivation.DeriveState(
                data.State,
                data.ReviewDecision,
                threads,
                hasMagicApproveFromAuthor: false,
                hasMagicChangesRequestedFromAuthor: false);

            var magicCount = MagicCommentDetector.Count(data.Comments.Select(c => c.Body));
            if (magicCount > 0)
            {
                (warnings ??= []).Add(MagicCommentDetector.FormatWarning(magicCount));
            }

            var mergeable = MapMergeable(data.Mergeable);
            var policy = ComputePolicy(state, mergeable);
            var metadata = includeMetadata ? PlanPrFrontMatter.Parse(data.Body) : null;

            // Flatten the top-level PR comments into the platform-neutral
            // shape for the analyzer to consume. Review (inline) comments
            // live inside the thread shape; this list is issue-comments
            // only, matching GraphQL's split.
            var topLevelComments = data.Comments.Select(MapTopLevelComment).ToList();
            var routing = PrPollTerminalRoute.Classify(state, reviewers, threads);

            var result = new PrPollStatusResult
            {
                PrUrl = prUrl,
                PrNumber = data.Number,
                RepoSlug = slug,
                State = state,
                Route = routing.Route,
                RouteReason = routing.Reason,
                HeadSha = data.HeadRefOid ?? string.Empty,
                HeadRef = data.HeadRefName ?? string.Empty,
                BaseRef = data.BaseRefName ?? string.Empty,
                Mergeable = mergeable,
                MergeCommitSha = data.MergeCommitSha,
                MergedAt = data.MergedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Reviewers = reviewers,
                Threads = threads,
                Comments = topLevelComments,
                AuthorIdentity = data.AuthorLogin ?? string.Empty,
                Policy = policy,
                Warnings = warnings,
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
    /// Map a single GitHub <see cref="GhReviewThread"/> to the
    /// platform-neutral <see cref="PrPollThread"/> shape. The thread's
    /// raw <c>isResolved</c> bit drives the <c>Status</c> string; ADO has
    /// a richer status enum so the field is platform-flavored. Each
    /// comment body is parsed through <see cref="PrCommentMarker.TryParse"/>
    /// so the analyzer can distinguish bot feedback from human feedback
    /// even when posted via the same operator token.
    /// </summary>
    private static PrPollThread MapGhThread(GhReviewThread t) => new()
    {
        Id = t.Id,
        IsResolved = t.IsResolved,
        IsOutdated = t.IsOutdated,
        Status = t.IsResolved ? "RESOLVED" : "UNRESOLVED",
        AuthorIdentity = string.IsNullOrEmpty(t.AuthorLogin) ? null : t.AuthorLogin,
        CreatedAt = t.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        CommentCount = t.CommentCount,
        Comments = t.Comments.Select(c => new PrPollComment
        {
            Author = c.AuthorLogin,
            Body = c.Body,
            CreatedAt = c.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            Marker = PrCommentMarker.TryParse(c.Body),
        }).ToList(),
    };

    /// <summary>
    /// Map a top-level GitHub PR comment (issue-comment surface, distinct
    /// from review-thread inline comments) to the platform-neutral
    /// <see cref="PrPollComment"/> shape. Parsed marker is the
    /// analyzer's bot-vs-human signal.
    /// </summary>
    private static PrPollComment MapTopLevelComment(GhPullRequestComment c) => new()
    {
        Author = c.AuthorLogin,
        Body = c.Body,
        CreatedAt = c.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        Marker = PrCommentMarker.TryParse(c.Body),
    };

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
            Route = string.Empty,
            RouteReason = string.Empty,
            HeadSha = string.Empty,
            HeadRef = string.Empty,
            BaseRef = string.Empty,
            Mergeable = null,
            Reviewers = [],
            Threads = [],
            Comments = [],
            AuthorIdentity = string.Empty,
            Policy = new PrPollPolicy { MergeAllowed = false, BlockingReasons = [message] },
            Error = message,
        };
        EmitPoll(result);
    }
}
