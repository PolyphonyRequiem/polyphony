using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// Per-item observation context shared by every requirement-kind composer
/// inside <see cref="StateCommands.NextReady"/>. Built once per work item;
/// passed to each composer so observers SHARE underlying I/O (slug
/// resolution, plan-PR list, plan-PR poll) instead of issuing them N
/// times — see the smoking-gun perf concern in
/// <c>files/closed-loop-state-plan.md §3.2</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plug-in contract for sibling observers (PR #3 ChildrenSeeded,
/// PR #4 Implementation, …).</b> When adding a new requirement-kind
/// observer that needs a per-item signal:
/// </para>
/// <list type="number">
///   <item><description>Add the signal as a property on this record (e.g.
///     <c>PlannedTagPresent</c> for ChildrenSeeded, <c>LatestImplPr</c> for
///     ImplementationMerged).</description></item>
///   <item><description>Fetch it inside
///     <see cref="StateCommands.BuildObservationScopeAsync"/> wrapped in
///     its own try/catch — failures must degrade to "no signal" with the
///     error captured in the corresponding <c>FetchError</c> property,
///     never thrown past the scope. <c>state next-ready</c> is a
///     routing-style verb and must always exit 0 with a structured
///     envelope.</description></item>
///   <item><description>Add a static composer on <see cref="StateCommands"/>
///     that maps the scope's signals to a
///     <c>(Disposition, Reason)</c> tuple, mirroring
///     <c>ComposePlanAuthored</c>/<c>ComposePlanReviewed</c>/
///     <c>ComposePlanPromoted</c>.</description></item>
///   <item><description>Wire the composer into
///     <see cref="StateCommands.BuildObservedFromSignals"/>.</description></item>
/// </list>
/// <para>
/// No re-architecture of the verb's outer shape is required for the next
/// two PRs in the closed-loop fix series. The fields below are mutable
/// by design — <see cref="StateCommands.BuildObservationScopeAsync"/>
/// fills them in piecewise as it issues each I/O call, then the scope is
/// frozen by being passed to the (read-only) composers.
/// </para>
/// </remarks>
internal sealed class NextReadyObservationScope
{
    /// <summary>The work item being observed.</summary>
    public required int ItemId { get; init; }

    /// <summary>The resolved root work-item id (= <see cref="ItemId"/> for
    /// an apex item; ancestor's id walked via parent chain otherwise).
    /// Drives <see cref="PlanBranch"/>.</summary>
    public required int RootId { get; init; }

    /// <summary>Canonical plan branch name for this item: <c>plan/{root}</c>
    /// when item == root, else <c>plan/{root}-{item}</c>. Empty when
    /// <see cref="RootId"/> or <see cref="ItemId"/> are non-positive.</summary>
    public required string PlanBranch { get; init; }

    // ── Plan-kind shared signals ────────────────────────────────────────

    /// <summary>GitHub <c>owner/repo</c> slug from
    /// <c>git remote get-url origin</c>. Empty when the remote could not
    /// be parsed (no origin, non-GitHub URL, transient git failure).
    /// When empty, all plan-kind composers degrade to Needed with a
    /// "no slug" reason.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Result of <c>git ls-remote --heads origin
    /// refs/heads/{plan_branch}</c>. False on absence OR transient
    /// ls-remote failure (failures degrade to "no signal" — observer
    /// posture from PR #1).</summary>
    public bool BranchExistsOnOrigin { get; set; }

    /// <summary>Highest-numbered plan PR for <see cref="PlanBranch"/>
    /// (open, closed, or merged), or null when none exist or the
    /// <c>gh pr list</c> call failed.</summary>
    public PullRequestSummary? LatestPlanPr { get; set; }

    /// <summary>Rich poll-data for <see cref="LatestPlanPr"/>'s number,
    /// fetched once and reused by plan_authored / plan_reviewed /
    /// plan_promoted. Null when no PR exists or <c>gh pr view</c>
    /// failed.</summary>
    public GhPullRequestPollData? PlanPrPoll { get; set; }

    /// <summary>Captured error message from the <c>gh pr list</c> call
    /// (or the underlying slug resolution), or null on success. When
    /// non-null, plan-kind composers force a Needed disposition with the
    /// error surfaced in the reason — distinguishing
    /// "couldn't observe" from "observed: no PR".</summary>
    public string? PlanPrFetchError { get; set; }

    /// <summary>Captured error message from the <c>gh pr view</c> call,
    /// or null on success. Same semantics as
    /// <see cref="PlanPrFetchError"/>.</summary>
    public string? PlanPrPollError { get; set; }
}
