using System.Collections.Generic;

namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr validate-plan-diff</c> — the Phase 3 P8b
/// pre-merge guard verb that classifies a plan PR's changed-file set
/// against the plan-tree taxonomy. Pure read; no side effects, no
/// manifest mutation, no platform-side merge attempt. The same
/// classification is invoked atomically inside
/// <c>polyphony pr merge-plan-pr</c> as the merge-time guard.
///
/// <para>Workflow consumers branch on the combination of
/// <see cref="Severity"/> and <see cref="Code"/>:</para>
/// <list type="bullet">
///   <item><c>Severity = "none"</c> — clean. Safe to merge or hand off.</item>
///   <item><c>Severity = "warning"</c> — advisory; merge allowed but
///     reviewers should see the message (e.g. <c>flag_set_no_parent_changes</c>
///     means the front-matter promised a parent change that didn't materialize).</item>
///   <item><c>Severity = "blocking"</c> — refuse the merge. Concrete
///     codes: <c>child_touched_polyphony_state</c>,
///     <c>child_touched_ancestor_plan</c>,
///     <c>child_touched_parent_plan</c>,
///     <c>missing_front_matter</c>,
///     <c>malformed_front_matter</c>.</item>
///   <item><c>Severity = "error"</c> — verb failed before classification
///     (PR not found, gh hang, slug unresolved). See <see cref="Error"/>.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrValidatePlanDiffResult
{
    /// <summary>Run's root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>Work-item id this plan PR belongs to.</summary>
    public required int ItemId { get; init; }

    /// <summary>Immediate plan-tree parent's id; 0 when this is the root plan.</summary>
    public required int ParentItemId { get; init; }

    /// <summary>PR number being validated.</summary>
    public required int PrNumber { get; init; }

    /// <summary>Owner/repo slug; empty when verb couldn't resolve it before erroring.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR head SHA at validation time; empty when poll failed.</summary>
    public required string HeadSha { get; init; }

    /// <summary>PR state at validation time (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>); empty when poll failed.</summary>
    public required string PrState { get; init; }

    /// <summary>One of <c>none</c>, <c>warning</c>, <c>blocking</c>, <c>error</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>Stable machine-readable code; <c>ok</c> on clean pass; populated for warning/blocking/error.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable explanation suitable for embedding in a PR comment or workflow log.</summary>
    public required string Message { get; init; }

    /// <summary>True when the PR's front-matter <c>requests_parent_change</c> flag was set.</summary>
    public required bool RequestsParentChange { get; init; }

    /// <summary>True when classification was actually performed (false for <see cref="Severity"/> = <c>error</c>).</summary>
    public required bool DiffClassified { get; init; }

    /// <summary>Self plan files touched (always allowed); empty list when none.</summary>
    public IReadOnlyList<string> SelfPlanFiles { get; init; } = System.Array.Empty<string>();

    /// <summary>Parent plan files touched (allowed only with <see cref="RequestsParentChange"/>).</summary>
    public IReadOnlyList<string> ParentPlanFiles { get; init; } = System.Array.Empty<string>();

    /// <summary>Ancestor plan files touched (always blocking — escalates to ancestor-plan workflow in P9).</summary>
    public IReadOnlyList<string> AncestorPlanFiles { get; init; } = System.Array.Empty<string>();

    /// <summary>Polyphony state files touched (<c>.polyphony/**</c>; always blocking).</summary>
    public IReadOnlyList<string> PolyphonyStateFiles { get; init; } = System.Array.Empty<string>();

    /// <summary>Other (implementation/code/doc) files touched; informational only.</summary>
    public IReadOnlyList<string> OtherFiles { get; init; } = System.Array.Empty<string>();

    /// <summary>Populated when <see cref="Severity"/> = <c>error</c>; null on classification outcomes.</summary>
    public string? Error { get; init; }
}
