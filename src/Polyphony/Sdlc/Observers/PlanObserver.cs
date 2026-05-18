using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
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
/// All four observation methods perform their own <see cref="RepoIdentity"/>
/// + PR list resolution. Callers that need multiple observations for the
/// same item should be aware that each call re-issues the underlying
/// <c>git remote get-url</c> + PR list + PR view chain; for the verb's
/// composite path see
/// <see cref="Polyphony.Commands.PlanCommands"/>'s detect-state
/// implementation, which uses the lower-level primitives below to fetch
/// each signal exactly once.
/// </para>
/// <para>
/// <b>Platform handling:</b> every observation method internally branches
/// on the resolved <see cref="RepoIdentity"/> variant — calls flow through
/// <see cref="IGhClient"/> for <see cref="RepoIdentity.GitHubRepo"/> and
/// through <see cref="IAdoClient"/> for <see cref="RepoIdentity.AdoRepo"/>.
/// Verbs that have already resolved an identity (with the platform-override
/// flags consumed by <see cref="RepoIdentityResolver"/>) should call the
/// <see cref="RepoIdentity"/>-overload variants directly to avoid a
/// redundant origin-URL probe.
/// </para>
/// </remarks>
public sealed class PlanObserver(
    IGitClient git,
    IGhClient gh,
    IAdoClient ado,
    ITwigClient twig,
    RepoIdentityResolver resolver)
{

    // ── Per-kind observation API (consumed by next-ready) ─────────────────

    /// <summary>
    /// Observe the <c>plan_authored</c> requirement: does a plan branch
    /// exist on origin and has a PR been opened against it?
    /// </summary>
    /// <remarks>
    /// These <c>Observe*Async</c> wrappers do <b>not</b> apply the
    /// run-watermark filter introduced in PR 1 of the run-reset family —
    /// they predate the per-item scope plumbing and have no apex-root
    /// context to read the <c>polyphony:run-started-at</c> tag from. The
    /// production verbs (<c>state next-ready</c>, <c>plan detect-state</c>)
    /// call the static <c>Map*</c> helpers directly with the watermark
    /// passed in via their own scope, so the filter is in effect on the
    /// hot path. The wrappers remain for legacy single-shot consumers
    /// (and the test suite); new callers should prefer the scope-aware
    /// next-ready pipeline. See <c>docs/decisions/run-reset.md</c>.
    /// </remarks>
    public Task<PlanAuthoredObservation> ObservePlanAuthoredAsync(
        int rootId, int itemId, CancellationToken ct = default)
        => ObservePlanAuthoredAsync(rootId, itemId, identity: null, ct);

    /// <summary>
    /// Identity-aware overload: when <paramref name="identity"/> is supplied
    /// (typically by a verb that has already consumed
    /// <c>--platform/--organization/--project/--repository</c> overrides
    /// via <see cref="RepoIdentityResolver"/>), the origin-URL probe is
    /// skipped. When null, the resolver is invoked with no overrides.
    /// </summary>
    public async Task<PlanAuthoredObservation> ObservePlanAuthoredAsync(
        int rootId, int itemId, RepoIdentity? identity, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        identity ??= await TryResolveRepoIdentityAsync(null, null, null, null, ct).ConfigureAwait(false);
        var branchExists = await CheckPlanBranchExistsAsync(planBranch, ct).ConfigureAwait(false);

        if (identity is null)
        {
            return new PlanAuthoredObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo identity from origin remote",
                PlanBranch: planBranch,
                BranchExistsOnOrigin: branchExists,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var latestPr = await GetLatestPlanPrAsync(identity, planBranch, ct).ConfigureAwait(false);
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

        var poll = await GetPlanPrPollAsync(identity, latestPr.Number, ct).ConfigureAwait(false);
        var prState = poll?.State?.ToUpperInvariant();
        return MapPlanAuthored(planBranch, branchExists, latestPr, prState);
    }

    /// <summary>
    /// Observe the <c>plan_reviewed</c> requirement: has the plan PR been
    /// approved?
    /// </summary>
    public Task<PlanReviewedObservation> ObservePlanReviewedAsync(
        int rootId, int itemId, CancellationToken ct = default)
        => ObservePlanReviewedAsync(rootId, itemId, identity: null, ct);

    /// <summary>
    /// Identity-aware overload: see
    /// <see cref="ObservePlanAuthoredAsync(int, int, RepoIdentity?, CancellationToken)"/>
    /// for the contract.
    /// </summary>
    public async Task<PlanReviewedObservation> ObservePlanReviewedAsync(
        int rootId, int itemId, RepoIdentity? identity, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        identity ??= await TryResolveRepoIdentityAsync(null, null, null, null, ct).ConfigureAwait(false);
        if (identity is null)
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo identity from origin remote",
                PrNumber: null,
                PrUrl: null,
                PrState: null,
                ReviewDecision: null);
        }

        var latestPr = await GetLatestPlanPrAsync(identity, planBranch, ct).ConfigureAwait(false);
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

        var poll = await GetPlanPrPollAsync(identity, latestPr.Number, ct).ConfigureAwait(false);
        return MapPlanReviewed(latestPr, poll);
    }

    /// <summary>
    /// Observe the <c>plan_promoted</c> requirement: has the plan PR been
    /// merged?
    /// </summary>
    public Task<PlanPromotedObservation> ObservePlanPromotedAsync(
        int rootId, int itemId, CancellationToken ct = default)
        => ObservePlanPromotedAsync(rootId, itemId, identity: null, ct);

    /// <summary>
    /// Identity-aware overload: see
    /// <see cref="ObservePlanAuthoredAsync(int, int, RepoIdentity?, CancellationToken)"/>
    /// for the contract.
    /// </summary>
    public async Task<PlanPromotedObservation> ObservePlanPromotedAsync(
        int rootId, int itemId, RepoIdentity? identity, CancellationToken ct = default)
    {
        var planBranch = ResolvePlanBranch(rootId, itemId);

        identity ??= await TryResolveRepoIdentityAsync(null, null, null, null, ct).ConfigureAwait(false);
        if (identity is null)
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo identity from origin remote",
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var latestPr = await GetLatestPlanPrAsync(identity, planBranch, ct).ConfigureAwait(false);
        if (latestPr is null)
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: $"no plan PR for branch '{planBranch}'",
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var poll = await GetPlanPrPollAsync(identity, latestPr.Number, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Observe the <c>implementation_merged</c> requirement: does an impl
    /// PR exist for the canonical <c>impl/{root}-{item}</c> branch and has
    /// it merged?
    /// </summary>
    /// <remarks>
    /// PR #4 scope: per-item impl PR only. The MG roll-up from
    /// closed-loop §3.1 row 5 (an MG item gated by all its impl children's
    /// PRs) is deferred to PR #5's cross-item reducer. See
    /// <see cref="ImplementationMergedObservation"/> for the deferral
    /// rationale.
    /// </remarks>
    public Task<ImplementationMergedObservation> ObserveImplementationMergedAsync(
        int rootId, int itemId, CancellationToken ct = default)
        => ObserveImplementationMergedAsync(rootId, itemId, identity: null, ct);

    /// <summary>
    /// Identity-aware overload: see
    /// <see cref="ObservePlanAuthoredAsync(int, int, RepoIdentity?, CancellationToken)"/>
    /// for the contract.
    /// </summary>
    public async Task<ImplementationMergedObservation> ObserveImplementationMergedAsync(
        int rootId, int itemId, RepoIdentity? identity, CancellationToken ct = default)
    {
        var implBranch = ResolveImplBranch(rootId, itemId);

        identity ??= await TryResolveRepoIdentityAsync(null, null, null, null, ct).ConfigureAwait(false);
        if (identity is null)
        {
            return new ImplementationMergedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve repo identity from origin remote",
                ImplBranch: implBranch,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        if (string.IsNullOrEmpty(implBranch))
        {
            return new ImplementationMergedObservation(
                Disposition: Disposition.Needed,
                Reason: "could not resolve impl branch (non-positive root or item id)",
                ImplBranch: implBranch,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var latestPr = await GetLatestImplPrAsync(identity, implBranch, ct).ConfigureAwait(false);
        if (latestPr is null)
        {
            return MapImplementationMerged(implBranch, latestPr: null, prState: null);
        }

        var poll = await GetPlanPrPollAsync(identity, latestPr.Number, ct).ConfigureAwait(false);
        var prState = poll?.State?.ToUpperInvariant();
        return MapImplementationMerged(implBranch, latestPr, prState);
    }

    // ── Pure mappers (no I/O) ─────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="prState"/> is <c>MERGED</c> and
    /// the PR's merge timestamp is at or before the run-started-at
    /// watermark — i.e. the PR is an artifact of a prior run and must
    /// not count towards satisfying the current run. Returns false when
    /// any of the three inputs is null (no watermark ⇒ no filter; no
    /// merge timestamp ⇒ cannot compare ⇒ pass through; non-merged PRs
    /// are not filtered here — open PRs created before reset are
    /// reset's responsibility to abandon, not the observer's).
    /// </summary>
    private static bool IsPriorRunMergedPr(
        string? prState,
        DateTimeOffset? mergedAt,
        DateTimeOffset? runStartedAtFilter)
    {
        return prState == "MERGED"
            && mergedAt is not null
            && runStartedAtFilter is not null
            && mergedAt.Value <= runStartedAtFilter.Value;
    }

    /// <summary>
    /// Format the "this PR is from a prior run" diagnostic reason
    /// surfaced on the observation. <paramref name="kind"/> is
    /// <c>plan</c> or <c>impl</c> for the user-facing prefix; the two
    /// timestamps are rendered with the round-trip ISO-8601 format so
    /// they round-trip via the tag writer and across machines.
    /// </summary>
    private static string FormatPriorRunReason(
        string kind, int prNumber, DateTimeOffset mergedAt, DateTimeOffset runStartedAt)
    {
        var merged = mergedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var started = runStartedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return $"{kind} PR #{prNumber} merged at {merged} is from a prior run (current run started at {started}); awaiting new {kind} PR";
    }

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
        string? prState,
        DateTimeOffset? mergedAt = null,
        DateTimeOffset? runStartedAtFilter = null)
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

        if (IsPriorRunMergedPr(prState, mergedAt, runStartedAtFilter))
        {
            return new PlanAuthoredObservation(
                Disposition: Disposition.Needed,
                Reason: FormatPriorRunReason("plan", latestPr.Number, mergedAt!.Value, runStartedAtFilter!.Value),
                PlanBranch: planBranch,
                BranchExistsOnOrigin: branchExists,
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState);
        }

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
        GhPullRequestPollData? poll,
        DateTimeOffset? runStartedAtFilter = null)
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

        if (IsPriorRunMergedPr(prState, poll.MergedAt, runStartedAtFilter))
        {
            return new PlanReviewedObservation(
                Disposition: Disposition.Needed,
                Reason: FormatPriorRunReason("plan", latestPr.Number, poll.MergedAt!.Value, runStartedAtFilter!.Value),
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState,
                ReviewDecision: review);
        }

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
        string? prState,
        DateTimeOffset? mergedAt = null,
        DateTimeOffset? runStartedAtFilter = null)
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

        if (IsPriorRunMergedPr(prState, mergedAt, runStartedAtFilter))
        {
            return new PlanPromotedObservation(
                Disposition: Disposition.Needed,
                Reason: FormatPriorRunReason("plan", latestPr.Number, mergedAt!.Value, runStartedAtFilter!.Value),
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState);
        }

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

    /// <summary>
    /// Map already-fetched primitives into an
    /// <see cref="ImplementationMergedObservation"/>. Mirrors
    /// <see cref="MapPlanPromoted"/>'s state→disposition table:
    /// <c>MERGED</c>=Satisfied, <c>OPEN</c>=Fulfilling, <c>CLOSED</c>=Needed,
    /// PR-exists-but-state-unknown=Fulfilling.
    /// </summary>
    public static ImplementationMergedObservation MapImplementationMerged(
        string implBranch,
        PullRequestSummary? latestPr,
        string? prState,
        DateTimeOffset? mergedAt = null,
        DateTimeOffset? runStartedAtFilter = null)
    {
        if (latestPr is null)
        {
            return new ImplementationMergedObservation(
                Disposition: Disposition.Needed,
                Reason: $"no impl PR for branch '{implBranch}'",
                ImplBranch: implBranch,
                PrNumber: null,
                PrUrl: null,
                PrState: null);
        }

        var prUrl = latestPr.Url ?? string.Empty;

        if (IsPriorRunMergedPr(prState, mergedAt, runStartedAtFilter))
        {
            return new ImplementationMergedObservation(
                Disposition: Disposition.Needed,
                Reason: FormatPriorRunReason("impl", latestPr.Number, mergedAt!.Value, runStartedAtFilter!.Value),
                ImplBranch: implBranch,
                PrNumber: latestPr.Number,
                PrUrl: prUrl,
                PrState: prState);
        }

        var (disposition, reason) = prState switch
        {
            "MERGED" => (Disposition.Satisfied, $"impl PR #{latestPr.Number} merged"),
            "OPEN" => (Disposition.Fulfilling, $"impl PR #{latestPr.Number} open; awaiting merge"),
            "CLOSED" => (Disposition.Needed, $"impl PR #{latestPr.Number} closed unmerged"),
            null => (Disposition.Fulfilling, $"impl PR #{latestPr.Number} state unavailable"),
            _ => (Disposition.Fulfilling, $"impl PR #{latestPr.Number} in unknown state '{prState}'"),
        };

        return new ImplementationMergedObservation(
            Disposition: disposition,
            Reason: reason,
            ImplBranch: implBranch,
            PrNumber: latestPr.Number,
            PrUrl: prUrl,
            PrState: prState);
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
    /// Compute the canonical impl-branch name for an item:
    /// <c>impl/{root}-{item}</c> per Rev 4 of the branch-model ADR
    /// (impl branches are flat — the enclosing MG is recorded by the
    /// impl PR's base branch, not the head name). Returns the empty
    /// string when either ID is non-positive. Delegates to
    /// <see cref="BranchNameBuilder.Impl"/> to avoid duplicating the
    /// grammar regex.
    /// </summary>
    /// <remarks>
    /// Closed-loop §3.1 row 5 uses the shorthand
    /// <c>impl/{root}/{mg}/{id}</c> in prose; the actual canonical name
    /// (and the format <c>polyphony branch ensure-impl</c> writes) is
    /// <c>impl/{root}-{item}</c>. The discrepancy is documented in the
    /// branch-model SKILL ("PR base branch carries the topology") and
    /// in <see cref="BranchNameBuilder.Impl"/>.
    /// </remarks>
    public static string ResolveImplBranch(int rootId, int itemId)
    {
        if (!RootId.TryParse(rootId, out var root)) return string.Empty;
        if (!WorkItemId.TryParse(itemId, out var item)) return string.Empty;
        return BranchNameBuilder.Impl(root, item).Value;
    }

    /// <summary>
    /// Resolve the GitHub <c>owner/repo</c> slug from
    /// <c>git remote get-url origin</c>. Returns the empty string on any
    /// failure.
    /// </summary>
    /// <remarks>
    /// Legacy GitHub-only API. New code should call
    /// <see cref="TryResolveRepoIdentityAsync"/> which understands ADO
    /// origin URLs and per-verb override flags. Retained because the
    /// <c>plan detect-state</c> verb path still consumes it directly while
    /// the ADO refactor lands one phase at a time.
    /// </remarks>
    public async Task<string> TryResolveSlugAsync(CancellationToken ct = default)
    {
        var identity = await TryResolveRepoIdentityAsync(null, null, null, null, ct).ConfigureAwait(false);
        return identity is RepoIdentity.GitHubRepo gh ? gh.Slug : string.Empty;
    }

    /// <summary>
    /// Resolve the active <see cref="RepoIdentity"/>, honouring optional
    /// <c>--platform/--organization/--project/--repository</c> overrides
    /// before falling back to <c>git remote get-url origin</c> URL parsing.
    /// Returns null when neither overrides nor the origin URL produce a
    /// recognised identity (matches the old slug API's "empty string"
    /// failure shape).
    /// </summary>
    public async Task<RepoIdentity?> TryResolveRepoIdentityAsync(
        string? overridePlatform,
        string? overrideOrganization,
        string? overrideProject,
        string? overrideRepository,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(
                overridePlatform ?? string.Empty,
                overrideOrganization ?? string.Empty,
                overrideProject ?? string.Empty,
                overrideRepository ?? string.Empty,
                ct)
                .ConfigureAwait(false);
            return resolved.Identity;
        }
        catch
        {
            return null;
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
    /// <remarks>
    /// GitHub-only legacy overload. Prefer
    /// <see cref="GetLatestPlanPrAsync(RepoIdentity, string, CancellationToken)"/>
    /// for new code so the call routes correctly on ADO.
    /// </remarks>
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
    /// <see cref="RepoIdentity"/>-aware overload that branches to
    /// <see cref="IGhClient"/> for GitHub repos and to
    /// <see cref="IAdoClient"/> for ADO repos. Both paths return the
    /// platform-neutral <see cref="PullRequestSummary"/> shape.
    /// </summary>
    public async Task<PullRequestSummary?> GetLatestPlanPrAsync(
        RepoIdentity identity, string planBranch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity switch
        {
            RepoIdentity.GitHubRepo gh => await GetLatestPlanPrAsync(gh.Slug, planBranch, ct).ConfigureAwait(false),
            RepoIdentity.AdoRepo ado => await GetLatestPrFromAdoAsync(ado, planBranch, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}"),
        };
    }

    /// <summary>
    /// Fetch the rich poll-data snapshot for a single PR via
    /// <c>gh pr view --json</c>. Returns null when the PR has disappeared
    /// between list and view.
    /// </summary>
    /// <remarks>
    /// GitHub-only legacy overload. Prefer
    /// <see cref="GetPlanPrPollAsync(RepoIdentity, int, CancellationToken)"/>
    /// for new code.
    /// </remarks>
    public Task<GhPullRequestPollData?> GetPlanPrPollAsync(
        string slug, int prNumber, CancellationToken ct = default)
        => gh.GetPullRequestPollDataAsync(slug, prNumber, ct);

    /// <summary>
    /// <see cref="RepoIdentity"/>-aware overload. ADO snapshots are
    /// converted to <see cref="GhPullRequestPollData"/> via
    /// <see cref="GhPullRequestPollAdapter"/> so downstream mappers stay
    /// platform-neutral.
    /// </summary>
    public async Task<GhPullRequestPollData?> GetPlanPrPollAsync(
        RepoIdentity identity, int prNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        switch (identity)
        {
            case RepoIdentity.GitHubRepo ghIdent:
                return await gh.GetPullRequestPollDataAsync(ghIdent.Slug, prNumber, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoIdent:
                var poll = await ado.GetPullRequestPollDataAsync(
                    adoIdent.Organization, adoIdent.Project, adoIdent.Repository, prNumber, ct)
                    .ConfigureAwait(false);
                return poll is null ? null : GhPullRequestPollAdapter.FromAdo(poll);
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>
    /// Return the highest-numbered PR (open, closed, or merged) for the
    /// impl branch <paramref name="implBranch"/>, or null when none exist.
    /// Same shape as <see cref="GetLatestPlanPrAsync(string, string, CancellationToken)"/>
    /// — the branch name is the only difference. Caller is responsible
    /// for handling <see cref="ExternalToolException"/> /
    /// <see cref="ExternalToolTimeoutException"/> when precise error
    /// reporting is required.
    /// </summary>
    public async Task<PullRequestSummary?> GetLatestImplPrAsync(
        string slug, string implBranch, CancellationToken ct = default)
    {
        var prs = await gh.ListPullRequestsAsync(
            slug,
            new PrListFilters(Head: implBranch, State: "all", Limit: 50),
            ct).ConfigureAwait(false);
        return prs.OrderByDescending(p => p.Number).FirstOrDefault();
    }

    /// <summary>
    /// <see cref="RepoIdentity"/>-aware overload. See
    /// <see cref="GetLatestPlanPrAsync(RepoIdentity, string, CancellationToken)"/>
    /// for the contract.
    /// </summary>
    public async Task<PullRequestSummary?> GetLatestImplPrAsync(
        RepoIdentity identity, string implBranch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity switch
        {
            RepoIdentity.GitHubRepo gh => await GetLatestImplPrAsync(gh.Slug, implBranch, ct).ConfigureAwait(false),
            RepoIdentity.AdoRepo ado => await GetLatestPrFromAdoAsync(ado, implBranch, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}"),
        };
    }

    private async Task<PullRequestSummary?> GetLatestPrFromAdoAsync(
        RepoIdentity.AdoRepo identity, string sourceBranch, CancellationToken ct)
    {
        var prs = await ado.ListPullRequestsAsync(
            identity.Organization, identity.Project, identity.Repository,
            AdoPullRequestStatus.All, sourceBranch, ct).ConfigureAwait(false);
        if (prs is null || prs.Count == 0) return null;

        var latest = prs.OrderByDescending(p => p.PullRequestId).FirstOrDefault();
        if (latest is null) return null;

        // ADO doesn't expose merge timestamp on the list endpoint; the
        // poll-data fetch is where MergedAt lands. Returning null here
        // matches the GH list-vs-view shape (gh pr list also omits
        // mergedAt; the field is filled in by the subsequent view).
        return new PullRequestSummary(
            Number: latest.PullRequestId,
            HeadRefName: StripRefsHeadsPrefix(latest.SourceRefName),
            Url: latest.Url,
            MergedAt: null);
    }

    private static string StripRefsHeadsPrefix(string refName)
    {
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.Ordinal)
            ? refName.Substring(prefix.Length)
            : refName;
    }

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

    /// <summary>
    /// Read the <c>polyphony:run-started-at</c> tag from
    /// <paramref name="rootId"/>'s work item via <c>twig show</c>.
    /// Returns null when the tag is absent OR the value is unparseable
    /// — callers MUST treat null as "no run-watermark in force" (legacy
    /// behavior; do not filter merged-PR observations).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="IsParentSeededAsync"/>, this method does NOT
    /// swallow twig failures. A failed <c>twig show</c> (process error
    /// → null JSON OR explicit exception) is rethrown as an
    /// <see cref="ExternalToolException"/> so the caller can distinguish
    /// "tag absent" (= no filter, legacy behavior, safe for fresh
    /// apexes) from "could not read tags" (= unsafe to fall back to
    /// no-filter on a reset apex — the caller MUST force observations
    /// to Needed). See <c>docs/decisions/run-reset.md</c> for the
    /// rationale.
    /// </para>
    /// <para>
    /// The tag lives on the apex root (the only writer is
    /// <c>polyphony reset state</c>). Reading via the same
    /// <c>twig show</c> primitive that <see cref="IsParentSeededAsync"/>
    /// uses keeps the two tag reads on a single twig surface.
    /// </para>
    /// </remarks>
    public async Task<DateTimeOffset?> ReadRunStartedAtAsync(int rootId, CancellationToken ct = default)
    {
        var json = await twig.ShowAsync(rootId, ct).ConfigureAwait(false);
        if (json is null)
        {
            // TwigClient.ShowAsync swallows process failures and returns
            // null. We need to distinguish that from a successful read
            // of a work item with no run-started-at tag — translate null
            // back into an exception so the caller's catch block fires.
            // The apex root must exist (next-ready already validated it
            // via the local repository), so null here is operationally
            // "twig is broken", not "no such item".
            throw new InvalidOperationException(
                $"twig show {rootId} returned no data (twig failure or unexpected empty response)");
        }

        var tagsRaw = json["tags"]?.GetValue<string?>() ?? string.Empty;
        var tags = Polyphony.Tagging.TagSet.Parse(tagsRaw);
        return Polyphony.Tagging.PolyphonyTags.ReadRunStartedAt(tags);
    }
}
