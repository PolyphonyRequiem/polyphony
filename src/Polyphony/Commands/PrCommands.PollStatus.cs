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
            var magicReviewers = ExtractMagicCommentReviewers(
                data.AuthorLogin, data.HeadRefOid ?? string.Empty, data.Comments);
            var reviewers = nativeReviewers.Concat(magicReviewers).ToList();

            var hasMagicApprove = HasAuthorMagicApproveOverridingNative(data.AuthorLogin, reviewers);
            var hasMagicChangesRequested = HasAuthorMagicChangesRequestedOverridingNative(data.AuthorLogin, reviewers);
            var state = PrPollStateDerivation.DeriveState(
                data.State, data.ReviewDecision, threads, hasMagicApprove, hasMagicChangesRequested);
            if (PrPollStateDerivation.MagicApproveContributed(
                    data.State, data.ReviewDecision, threads, hasMagicApprove, hasMagicChangesRequested))
            {
                // Only the bare-form fallback gets a deprecation warning;
                // the SHA-bound form is canonical and self-invalidates on
                // new commits, so it does not need to nag the user.
                var winner = magicReviewers.FirstOrDefault();
                if (winner is not null && winner.Source == "magic_comment")
                {
                    (warnings ??= []).Add(
                        PrPollStateDerivation.FormatNoShaDeprecationWarning(data.HeadRefOid ?? string.Empty));
                }
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
    /// magic comment (in either the SHA-bound or bare form) that is more
    /// recent than any native APPROVED | CHANGES_REQUESTED review they
    /// themselves left, AND that magic vote is itself an approval (not a
    /// <c>polyphony:request-changes</c>).
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
            if (IsMagicCommentSource(r.Source))
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

    /// <summary>
    /// True when the PR author has posted a SHA-bound
    /// <c>polyphony:request-changes</c> magic comment whose SHA matches
    /// the current head, AND that magic vote is more recent than any
    /// native review the author left (or any author <c>polyphony:approve</c>
    /// — both are represented in the same synthesised reviewer slot since
    /// <see cref="ExtractMagicCommentReviewers"/> picks only the most
    /// recent author magic comment).
    ///
    /// <para>This is the GitHub-author self-block path. Authors cannot
    /// <c>REQUEST_CHANGES</c> on their own PRs natively, and review
    /// threads only work for non-author reviewers — so the SHA-bound
    /// magic comment is the only way an author can pause merge of their
    /// own PR. The SHA binding is mandatory (no bare-form fallback) so
    /// the comment self-invalidates on any new push and cannot become
    /// the permanent-loop trigger that retired the original
    /// <c>polyphony:request-changes</c> form.</para>
    /// </summary>
    private static bool HasAuthorMagicChangesRequestedOverridingNative(
        string prAuthorLogin,
        IReadOnlyList<PrPollReviewer> reviewers)
    {
        if (string.IsNullOrEmpty(prAuthorLogin)) return false;

        PrPollReviewer? magicVote = null;
        DateTimeOffset? authorNativeAt = null;
        foreach (var r in reviewers)
        {
            if (!string.Equals(r.Identity, prAuthorLogin, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsMagicCommentSource(r.Source))
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
        if (magicVote.Vote != "changes_requested") return false;
        if (authorNativeAt is null) return true;
        return TryParseRoundtrip(magicVote.SubmittedAt, out var magicAt) && magicAt > authorNativeAt;
    }

    private static bool IsMagicCommentSource(string? source)
        => source == "magic_comment"
        || source == "magic_comment_sha_bound"
        || source == "magic_comment_request_changes";

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
    /// Recognized magic-comment syntax for the approve path.
    ///
    /// <para>Two recognized shapes:</para>
    /// <list type="bullet">
    ///   <item><c>polyphony:approve &lt;sha&gt;</c> — canonical SHA-bound form. The SHA pins the approval to a specific commit; any new push silently invalidates it (no timestamp comparison required). Captured in the <c>sha</c> named group.</item>
    ///   <item><c>polyphony:approve</c> — bare deprecation fallback. Still recognized but emits a warning recommending the SHA-bound form so the author can paste the canonical comment.</item>
    /// </list>
    ///
    /// <para>Anchored to start of line, optional whitespace, case-insensitive.
    /// Trailing text on the same line is allowed (e.g.
    /// <c>"polyphony:approve abc1234 LGTM, ship it"</c>). The lookahead
    /// ensures we only match a 7+ hex run as a SHA when it is a discrete
    /// token (followed by whitespace or end of line) — narrative text like
    /// <c>"polyphony:approve abcdefg789 because..."</c> still binds the
    /// SHA capture but trailing English continues without re-matching.</para>
    ///
    /// <para>Restricted to PR-author comments (single-user mode). Comments
    /// from other identities are ignored — they should leave a native
    /// review (or open a thread for a blocking concern).</para>
    /// </summary>
    private static readonly Regex MagicApproveRegex = new(
        @"^\s*polyphony:approve(?:\s+(?<sha>[0-9a-fA-F]{7,40}))?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Recognized magic-comment syntax for the author self-block path.
    ///
    /// <para><b>SHA is mandatory.</b> Unlike the approve form, there is
    /// no bare-form fallback. The original bare
    /// <c>polyphony:request-changes</c> was retired in option B (issue
    /// #207) precisely because it became a permanent loop trigger — the
    /// comment persisted across polls and forced <c>changes_requested</c>
    /// forever until the user manually deleted it. The SHA-bound form is
    /// safe by construction: any new commit invalidates the comment
    /// silently, so a remediation push automatically clears the block.</para>
    ///
    /// <para>This is the GitHub-author self-block path. Authors cannot
    /// natively <c>REQUEST_CHANGES</c> on their own PRs, and the
    /// review-thread escape hatch only works for non-author reviewers.
    /// The SHA-bound magic comment is the only mechanism an author has
    /// to pause merge of their own PR.</para>
    ///
    /// <para>Restricted to PR-author comments (single-user mode). Comments
    /// from other identities are ignored — they should leave a native
    /// review or open a thread.</para>
    /// </summary>
    private static readonly Regex MagicRequestChangesRegex = new(
        @"^\s*polyphony:request-changes\s+(?<sha>[0-9a-fA-F]{7,40})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Scan PR-author top-level comments for either the
    /// <c>polyphony:approve</c> or <c>polyphony:request-changes</c>
    /// magic-comment vote. Returns at most one synthetic reviewer entry —
    /// the most recent author magic-comment vote of either form that
    /// contributes to derivation. The synthetic reviewer's <c>Source</c>
    /// field encodes which form won:
    /// <list type="bullet">
    ///   <item><c>magic_comment_sha_bound</c> — a SHA-bound approve comment whose captured SHA is a case-insensitive prefix of the current <paramref name="headSha"/>. Canonical (no warning).</item>
    ///   <item><c>magic_comment</c> — a bare-form approve comment (no SHA). Triggers a deprecation warning at the call site.</item>
    ///   <item><c>magic_comment_request_changes</c> — a SHA-bound request-changes comment whose SHA matches the current head. Canonical (no warning).</item>
    /// </list>
    /// SHA-bound comments whose SHA does NOT match the current head are
    /// ignored entirely — that is the structural self-invalidation rule.
    /// When multiple forms are present, the most recent timestamp wins;
    /// ties between SHA-bound and bare-approve favour the SHA-bound form.
    ///
    /// <para>Returns an empty list when:</para>
    /// <list type="bullet">
    ///   <item>The PR author login is unknown (gh omitted the field).</item>
    ///   <item>No author comments contain a recognized magic-comment line that contributes (only stale SHA-bound matches found, or no matches at all).</item>
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
        string headSha,
        IReadOnlyList<GhPullRequestComment> comments)
    {
        if (string.IsNullOrEmpty(authorLogin) || comments.Count == 0)
            return Array.Empty<PrPollReviewer>();

        DateTimeOffset? mostRecentShaBoundApproveAt = null;
        DateTimeOffset? mostRecentBareApproveAt = null;
        DateTimeOffset? mostRecentRequestChangesAt = null;
        foreach (var c in comments)
        {
            if (!string.Equals(c.AuthorLogin, authorLogin, StringComparison.OrdinalIgnoreCase))
                continue;
            if (c.CreatedAt is not { } at) continue;

            // Per-comment, evaluate every line that matches — a single
            // comment containing both bare and SHA-bound lines should
            // contribute both signals. (Vanishingly rare in practice but
            // harmless.)
            foreach (Match match in MagicApproveRegex.Matches(c.Body))
            {
                var shaCapture = match.Groups["sha"];
                if (shaCapture.Success)
                {
                    if (IsShaPrefixMatch(shaCapture.Value, headSha))
                    {
                        if (mostRecentShaBoundApproveAt is null || mostRecentShaBoundApproveAt < at)
                        {
                            mostRecentShaBoundApproveAt = at;
                        }
                    }
                    // Stale SHA-bound match: silently ignored (structural
                    // self-invalidation). The user can re-approve by posting
                    // a fresh comment with the current head SHA.
                }
                else
                {
                    if (mostRecentBareApproveAt is null || mostRecentBareApproveAt < at)
                    {
                        mostRecentBareApproveAt = at;
                    }
                }
            }

            foreach (Match match in MagicRequestChangesRegex.Matches(c.Body))
            {
                // Regex requires SHA capture — no bare-form path here.
                var shaCapture = match.Groups["sha"];
                if (!shaCapture.Success) continue;
                if (!IsShaPrefixMatch(shaCapture.Value, headSha)) continue;
                if (mostRecentRequestChangesAt is null || mostRecentRequestChangesAt < at)
                {
                    mostRecentRequestChangesAt = at;
                }
            }
        }

        // Pick the single most-recent author magic vote across all forms.
        // Tie-break favours SHA-bound approve over bare approve (existing
        // rule); request-changes wins on strict-most-recent because it's
        // the explicit retraction signal.
        DateTimeOffset? winnerAt = null;
        string winnerVote = "";
        string winnerSource = "";

        if (mostRecentRequestChangesAt is not null)
        {
            winnerAt = mostRecentRequestChangesAt;
            winnerVote = "changes_requested";
            winnerSource = "magic_comment_request_changes";
        }

        if (mostRecentShaBoundApproveAt is not null
            && (winnerAt is null || mostRecentShaBoundApproveAt > winnerAt))
        {
            winnerAt = mostRecentShaBoundApproveAt;
            winnerVote = "approved";
            winnerSource = "magic_comment_sha_bound";
        }

        if (mostRecentBareApproveAt is not null
            && (winnerAt is null || mostRecentBareApproveAt > winnerAt))
        {
            winnerAt = mostRecentBareApproveAt;
            winnerVote = "approved";
            winnerSource = "magic_comment";
        }

        if (winnerAt is null) return Array.Empty<PrPollReviewer>();

        return [
            new PrPollReviewer
            {
                Identity = authorLogin,
                Vote = winnerVote,
                SubmittedAt = winnerAt.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Source = winnerSource,
            },
        ];
    }

    /// <summary>
    /// Case-insensitive prefix-match between a captured SHA from a magic
    /// comment and the PR's current head SHA. Accepts the canonical 40-char
    /// form, the conventional 7-char short form, and anything in between.
    /// </summary>
    private static bool IsShaPrefixMatch(string captured, string headSha)
    {
        if (string.IsNullOrEmpty(captured) || string.IsNullOrEmpty(headSha)) return false;
        if (captured.Length > headSha.Length) return false;
        return headSha.StartsWith(captured, StringComparison.OrdinalIgnoreCase);
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
