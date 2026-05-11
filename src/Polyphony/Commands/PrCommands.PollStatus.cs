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

            // Build review-thread snapshot. Threads are now the source of
            // truth for the changes_requested gate (issue #207). Failure
            // to fetch threads is non-fatal — the verb still emits a
            // result envelope, but appends a warning so consumers know
            // the threads field may not reflect reality.
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

            // Build native-review reviewer list, then synthesize an
            // author-approve magic-comment reviewer entry (only the
            // approve form survives — request-changes is no longer
            // recognized; reviewers should leave a review thread).
            var nativeReviewers = data.Reviews.Select(MapReviewer).ToList();
            var magicReviewers = ExtractMagicCommentReviewers(data.AuthorLogin, data.Comments);
            var reviewers = nativeReviewers.Concat(magicReviewers).ToList();

            var hasMagicApprove = HasAuthorMagicApproveOverridingNative(data.AuthorLogin, reviewers);
            var state = PrPollStateDerivation.DeriveState(
                data.State, data.ReviewDecision, threads, hasMagicApprove);
            if (PrPollStateDerivation.MagicApproveContributed(
                    data.State, data.ReviewDecision, threads, hasMagicApprove))
            {
                (warnings ??= []).Add(PrPollStateDerivation.MagicCommentDeprecationWarning);
            }

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
                Threads = threads,
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
    /// a richer status enum so the field is platform-flavored.
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
    };

    /// <summary>
    /// True when the PR author has posted a <c>polyphony:approve</c>
    /// magic comment that is more recent than any native APPROVED |
    /// CHANGES_REQUESTED review they themselves left. Mirrors the older
    /// per-identity overlay logic, scoped to just the approve path —
    /// <c>polyphony:request-changes</c> was retired in option B because
    /// it was a permanent-loop trigger.
    /// </summary>
    private static bool HasAuthorMagicApproveOverridingNative(
        string prAuthorLogin,
        IReadOnlyList<PrPollReviewer> reviewers)
    {
        if (string.IsNullOrEmpty(prAuthorLogin)) return false;

        PrPollReviewer? magicVote = null;
        DateTimeOffset? authorNativeAt = null;
        foreach (var r in reviewers)
        {
            if (!string.Equals(r.Identity, prAuthorLogin, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Source == "magic_comment")
            {
                magicVote = r;
                continue;
            }
            if (r.Vote != "approved" && r.Vote != "changes_requested") continue;
            if (!TryParseRoundtrip(r.SubmittedAt, out var nativeAt)) continue;
            if (authorNativeAt is null || authorNativeAt < nativeAt)
            {
                authorNativeAt = nativeAt;
            }
        }

        if (magicVote is null) return false;
        if (magicVote.Vote != "approved") return false;
        if (authorNativeAt is null) return true;
        return TryParseRoundtrip(magicVote.SubmittedAt, out var magicAt) && magicAt > authorNativeAt;
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
    /// Recognized magic-comment syntax — only the approve form survives
    /// after option B (issue #207). The <c>polyphony:request-changes</c>
    /// form has been retired: it was a permanent loop trigger because the
    /// comment persisted across polls, and it has been superseded by
    /// PR review threads (which have native resolved state).
    ///
    /// <para>Anchored to start of line, optional whitespace, case-insensitive.
    /// Trailing text on the same line is allowed (e.g. "polyphony:approve LGTM, ship it").</para>
    ///
    /// <para>Restricted to PR-author comments (single-user mode). Comments
    /// from other identities are ignored — they should leave a native
    /// review (or open a thread for a blocking concern).</para>
    /// </summary>
    private static readonly Regex MagicApproveRegex = new(
        @"^\s*polyphony:approve\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Scan PR-author top-level comments for the deprecated
    /// <c>polyphony:approve</c> magic-comment vote. Returns at most one
    /// synthetic reviewer entry (the author's most recent magic-approve
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

        // Per author, track the most recent magic-approve comment by createdAt.
        // (Single-user mode; a future change can extend this to a configurable
        // set of identities.)
        DateTimeOffset? mostRecentApproveAt = null;
        foreach (var c in comments)
        {
            if (!string.Equals(c.AuthorLogin, authorLogin, StringComparison.OrdinalIgnoreCase))
                continue;
            if (c.CreatedAt is not { } at) continue;
            if (!MagicApproveRegex.IsMatch(c.Body)) continue;

            if (mostRecentApproveAt is null || mostRecentApproveAt < at)
            {
                mostRecentApproveAt = at;
            }
        }

        if (mostRecentApproveAt is null) return Array.Empty<PrPollReviewer>();

        return [
            new PrPollReviewer
            {
                Identity = authorLogin,
                Vote = "approved",
                SubmittedAt = mostRecentApproveAt.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
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
            Threads = [],
            Policy = new PrPollPolicy { MergeAllowed = false, BlockingReasons = [message] },
            Error = message,
        };
        EmitPoll(result);
    }
}
