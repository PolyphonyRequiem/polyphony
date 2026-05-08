using System.Text.RegularExpressions;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observes the live state of the plan branch / plan PR / planned-tag for a
/// single work item and produces per-<see cref="RequirementKind"/>
/// observations. The single source of truth for plan-state semantics —
/// shared by the <c>polyphony plan detect-state</c> verb (which composes
/// the observations into a legacy state-machine string for
/// <c>plan-level.yaml</c>) and by <c>polyphony state next-ready</c>
/// (which feeds the per-kind observations directly into the
/// <see cref="RequirementSetReducer"/>).
/// </summary>
/// <remarks>
/// <para>
/// Failure posture matches the existing <c>plan detect-state</c> behaviour:
/// transient I/O failures (<c>gh</c>, <c>git</c>, <c>twig</c>) degrade to
/// "no observation" rather than throw. Observers may emit
/// <see cref="Disposition.Needed"/> /
/// <see cref="Disposition.Fulfilling"/> /
/// <see cref="Disposition.Satisfied"/> only — never
/// <see cref="Disposition.Ready"/> (per
/// <see cref="ObservedRequirementState"/>'s contract).
/// </para>
/// <para>
/// All four observation methods perform their own slug + PR list
/// resolution. Callers that need multiple observations for the same item
/// should be aware that each call re-issues the underlying
/// <c>git remote get-url</c> + <c>gh pr list</c> + <c>gh pr view</c> chain;
/// for the verb's composite path see
/// <see cref="Polyphony.Commands.PlanCommands"/>'s detect-state
/// implementation, which uses the lower-level primitives below to fetch
/// each signal exactly once.
/// </para>
/// </remarks>
public sealed class PlanObserver(IGitClient git, IGhClient gh, ITwigClient twig)
{
    private static readonly Regex GitHubSlugRegex =
        new(@"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?/?$", RegexOptions.Compiled);

    // ── Per-kind observation API (consumed by next-ready) ─────────────────

    /// <summary>
    /// Observe the <c>plan_authored</c> requirement: does a plan branch
    /// exist on origin and has a PR been opened against it?
    /// </summary>
    public async Task<PlanAuthoredObservation> ObservePlanAuthoredAsync(
        int rootId, int itemId, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        var branchExists = await CheckPlanBranchExistsAsync(planBranch, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(slug))
        {
            return new PlanAuthoredObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo slug from origin remote",
                PlanBranch: planBranch,
                BranchExistsOnOrigin: branchExists,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var latestPr = await GetLatestPlanPrAsync(slug, planBranch, ct).ConfigureAwait(false);
        if (latestPr is null)
        {
            return new PlanAuthoredObservation(
                Disposition: Disposition.Needed,
                Reason: branchExists
                    ? $"plan branch '{planBranch}' exists on origin but no PR has been opened"
                    : $"no plan branch '{planBranch}' on origin and no PR opened",
                PlanBranch: planBranch,
                BranchExistsOnOrigin: branchExists,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var poll = await GetPlanPrPollAsync(slug, latestPr.Number, ct).ConfigureAwait(false);
        var prState = poll?.State?.ToUpperInvariant();
        return MapPlanAuthored(planBranch, branchExists, latestPr, prState);
    }

    /// <summary>
    /// Observe the <c>plan_reviewed</c> requirement: has the plan PR been
    /// approved?
    /// </summary>
    public async Task<PlanReviewedObservation> ObservePlanReviewedAsync(
        int rootId, int itemId, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo slug from origin remote",
                PrNumber: null,
                PrUrl: null,
                PrState: null,
                ReviewDecision: null);
        }

        var latestPr = await GetLatestPlanPrAsync(slug, planBranch, ct).ConfigureAwait(false);
        if (latestPr is null)
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Needed,
                Reason: $"no plan PR for branch '{planBranch}'",
                PrNumber: null,
                PrUrl: null,
                PrState: null,
                ReviewDecision: null);
        }

        var poll = await GetPlanPrPollAsync(slug, latestPr.Number, ct).ConfigureAwait(false);
        return MapPlanReviewed(latestPr, poll);
    }

    /// <summary>
    /// Observe the <c>plan_promoted</c> requirement: has the plan PR been
    /// merged?
    /// </summary>
    public async Task<PlanPromotedObservation> ObservePlanPromotedAsync(
        int rootId, int itemId, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo slug from origin remote",
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var latestPr = await GetLatestPlanPrAsync(slug, planBranch, ct).ConfigureAwait(false);
        if (latestPr is null)
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: $"no plan PR for branch '{planBranch}'",
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var poll = await GetPlanPrPollAsync(slug, latestPr.Number, ct).ConfigureAwait(false);
        var prState = poll?.State?.ToUpperInvariant();
        return MapPlanPromoted(latestPr, prState);
    }

    /// <summary>
    /// Observe the <c>children_seeded</c> requirement: is the
    /// <paramref name="plannedTag"/> tag (default
    /// <c>polyphony:planned</c>) present on the work item? The tag is the
    /// canonical write-once side effect of <c>plan seed-children</c> — see
    /// <see cref="ChildrenSeededObservation"/> for the rationale.
    /// </summary>
    public async Task<ChildrenSeededObservation> ObserveChildrenSeededAsync(
        int itemId, string plannedTag = "polyphony:planned", CancellationToken ct = default)
    {
        var present = await IsParentSeededAsync(itemId, plannedTag, ct).ConfigureAwait(false);
        return MapChildrenSeeded(present, plannedTag);
    }

    // ── Pure mappers (no I/O) ─────────────────────────────────────────────

    /// <summary>
    /// Map already-fetched primitives into a
    /// <see cref="PlanAuthoredObservation"/>. Exposed so the verb can
    /// fetch each signal exactly once and still produce per-kind
    /// observations.
    /// </summary>
    public static PlanAuthoredObservation MapPlanAuthored(
        string planBranch,
        bool branchExists,
        PullRequestSummary? latestPr,
        string? prState)
    {
        if (latestPr is null)
        {
            return new PlanAuthoredObservation(
                Disposition: Disposition.Needed,
                Reason: branchExists
                    ? $"plan branch '{planBranch}' exists on origin but no PR has been opened"
                    : $"no plan branch '{planBranch}' on origin and no PR opened",
                PlanBranch: planBranch,
                BranchExistsOnOrigin: branchExists,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var prUrl = latestPr.Url ?? string.Empty;
        var disposition = prState switch
        {
            "MERGED" => Disposition.Satisfied,
            "OPEN" => Disposition.Fulfilling,
            "CLOSED" => Disposition.Needed,
            _ => Disposition.Fulfilling, // PR exists; treat unknown state as still in flight.
        };
        var reason = prState switch
        {
            "MERGED" => $"plan PR #{latestPr.Number} merged",
            "OPEN" => $"plan PR #{latestPr.Number} open",
            "CLOSED" => $"plan PR #{latestPr.Number} closed unmerged; replan required",
            null => $"plan PR #{latestPr.Number} state unavailable",
            _ => $"plan PR #{latestPr.Number} in unknown state '{prState}'",
        };

        return new PlanAuthoredObservation(
            Disposition: disposition,
            Reason: reason,
            PlanBranch: planBranch,
            BranchExistsOnOrigin: branchExists,
            PrNumber: latestPr.Number,
            PrUrl: prUrl,
            PrState: prState);
    }

    /// <summary>
    /// Map already-fetched primitives into a <see cref="PlanReviewedObservation"/>.
    /// </summary>
    public static PlanReviewedObservation MapPlanReviewed(
        PullRequestSummary? latestPr,
        GhPullRequestPollData? poll)
    {
        if (latestPr is null || poll is null)
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Needed,
                Reason: latestPr is null
                    ? "no plan PR opened"
                    : $"plan PR #{latestPr.Number} state unavailable",
                PrNumber: latestPr?.Number,
                PrUrl: latestPr?.Url,
                PrState: null,
                ReviewDecision: null);
        }

        var prState = poll.State?.ToUpperInvariant();
        var review = poll.ReviewDecision ?? string.Empty;
        var prUrl = latestPr.Url ?? string.Empty;

        // Merged ⇒ implicitly past review (a merged PR cannot be unreviewed).
        if (prState == "MERGED")
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Satisfied,
                Reason: $"plan PR #{latestPr.Number} merged (implies approved)",
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState,
                ReviewDecision: review);
        }

        if (prState == "OPEN" && string.Equals(review, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Satisfied,
                Reason: $"plan PR #{latestPr.Number} approved",
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState,
                ReviewDecision: review);
        }

        if (prState == "OPEN")
        {
            var dec = string.IsNullOrEmpty(review) ? "REVIEW_REQUIRED" : review;
            return new PlanReviewedObservation(
                Disposition: Disposition.Fulfilling,
                Reason: $"plan PR #{latestPr.Number} open; review decision is {dec}",
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState,
                ReviewDecision: review);
        }

        // CLOSED unmerged ⇒ review effort restarts with a new PR.
        return new PlanReviewedObservation(
            Disposition: Disposition.Needed,
            Reason: $"plan PR #{latestPr.Number} closed unmerged; review restarts on next PR",
            PrNumber: latestPr.Number,
            PrUrl: prUrl,
            PrState: prState,
            ReviewDecision: review);
    }

    /// <summary>
    /// Map already-fetched primitives into a <see cref="PlanPromotedObservation"/>.
    /// </summary>
    public static PlanPromotedObservation MapPlanPromoted(
        PullRequestSummary? latestPr,
        string? prState)
    {
        if (latestPr is null)
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: "no plan PR opened",
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var prUrl = latestPr.Url ?? string.Empty;
        var (disposition, reason) = prState switch
        {
            "MERGED" => (Disposition.Satisfied, $"plan PR #{latestPr.Number} merged"),
            "OPEN" => (Disposition.Fulfilling, $"plan PR #{latestPr.Number} open; awaiting merge"),
            "CLOSED" => (Disposition.Needed, $"plan PR #{latestPr.Number} closed unmerged"),
            null => (Disposition.Fulfilling, $"plan PR #{latestPr.Number} state unavailable"),
            _ => (Disposition.Fulfilling, $"plan PR #{latestPr.Number} in unknown state '{prState}'"),
        };

        return new PlanPromotedObservation(
            Disposition: disposition,
            Reason: reason,
            PrNumber: latestPr.Number,
            PrUrl: prUrl,
            PrState: prState);
    }

    /// <summary>
    /// Map an already-checked tag presence into a
    /// <see cref="ChildrenSeededObservation"/>.
    /// </summary>
    public static ChildrenSeededObservation MapChildrenSeeded(bool tagPresent, string plannedTag)
    {
        return new ChildrenSeededObservation(
            Disposition: tagPresent ? Disposition.Satisfied : Disposition.Needed,
            Reason: tagPresent
                ? $"tag '{plannedTag}' present on work item"
                : $"tag '{plannedTag}' not present on work item",
            TagPresent: tagPresent);
    }

    // ── Lower-level primitives (shared with the verb) ─────────────────────

    /// <summary>
    /// Compute the canonical plan-branch name for an item:
    /// <c>plan/{root}</c> when <paramref name="itemId"/> equals
    /// <paramref name="rootId"/>, otherwise <c>plan/{root}-{item}</c>.
    /// Returns the empty string when either ID is non-positive.
    /// </summary>
    public static string ResolvePlanBranch(int rootId, int itemId)
    {
        if (!RootId.TryParse(rootId, out var root)) return string.Empty;
        if (!WorkItemId.TryParse(itemId, out var item)) return string.Empty;
        return rootId == itemId
            ? BranchNameBuilder.RootPlan(root).Value
            : BranchNameBuilder.DescendantPlan(root, item).Value;
    }

    /// <summary>
    /// Resolve the GitHub <c>owner/repo</c> slug from
    /// <c>git remote get-url origin</c>. Returns the empty string on any
    /// failure.
    /// </summary>
    public async Task<string> TryResolveSlugAsync(CancellationToken ct = default)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return string.Empty;
            var match = GitHubSlugRegex.Match(url);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns true when <c>git ls-remote --heads origin refs/heads/{planBranch}</c>
    /// reports at least one match. Returns false on transient
    /// <see cref="ExternalToolException"/>; callers that need to distinguish
    /// "absent" from "could not check" must use
    /// <see cref="IGitClient.LsRemoteHeadsAsync"/> directly.
    /// </summary>
    public async Task<bool> CheckPlanBranchExistsAsync(string planBranch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(planBranch)) return false;
        try
        {
            var heads = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{planBranch}", ct)
                .ConfigureAwait(false);
            return heads.Count > 0;
        }
        catch (ExternalToolException)
        {
            return false;
        }
    }

    /// <summary>
    /// Variant of <see cref="CheckPlanBranchExistsAsync"/> that surfaces the
    /// underlying <see cref="ExternalToolException"/> instead of swallowing
    /// it. Used by the verb so it can report a precise error JSON when
    /// ls-remote fails.
    /// </summary>
    public async Task<bool> CheckPlanBranchExistsOrThrowAsync(string planBranch, CancellationToken ct = default)
    {
        var heads = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{planBranch}", ct)
            .ConfigureAwait(false);
        return heads.Count > 0;
    }

    /// <summary>
    /// Return the highest-numbered PR (open, closed, or merged) for
    /// <paramref name="planBranch"/>, or null when none exist. Caller is
    /// responsible for handling <see cref="ExternalToolException"/> /
    /// <see cref="ExternalToolTimeoutException"/> when precise error
    /// reporting is required.
    /// </summary>
    public async Task<PullRequestSummary?> GetLatestPlanPrAsync(
        string slug, string planBranch, CancellationToken ct = default)
    {
        var prs = await gh.ListPullRequestsAsync(
            slug,
            new PrListFilters(Head: planBranch, State: "all", Limit: 50),
            ct).ConfigureAwait(false);
        return prs.OrderByDescending(p => p.Number).FirstOrDefault();
    }

    /// <summary>
    /// Fetch the rich poll-data snapshot for a single PR via
    /// <c>gh pr view --json</c>. Returns null when the PR has disappeared
    /// between list and view.
    /// </summary>
    public Task<GhPullRequestPollData?> GetPlanPrPollAsync(
        string slug, int prNumber, CancellationToken ct = default)
        => gh.GetPullRequestPollDataAsync(slug, prNumber, ct);

    /// <summary>
    /// Returns true when the work item carries the
    /// <paramref name="plannedTag"/> tag (the canonical write-once
    /// side-effect of <c>plan seed-children</c>). Returns false on
    /// absence OR on twig lookup failure — the existing detect-state
    /// posture (and matches what callers downstream expect: "not seeded"
    /// is the safe default that reschedules the seeder).
    /// </summary>
    public async Task<bool> IsParentSeededAsync(int itemId, string plannedTag, CancellationToken ct = default)
    {
        try
        {
            var json = await twig.ShowAsync(itemId, ct).ConfigureAwait(false);
            if (json is null) return false;

            // twig show emits {"id":N,"tags":"a;b;c", ...}. Tags are a
            // semicolon-separated string in System.Tags' canonical form.
            var tags = json["tags"]?.GetValue<string?>() ?? string.Empty;
            return tags
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(t => string.Equals(t, plannedTag, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
