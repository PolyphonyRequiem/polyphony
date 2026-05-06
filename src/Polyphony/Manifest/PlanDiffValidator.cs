using System.Collections.Generic;

namespace Polyphony.Manifest;

/// <summary>
/// Phase 3 P8b: classifies a plan-PR's changed-file set against the
/// plan-tree taxonomy and returns a severity-coded violation (or clean
/// pass). Pure function — no I/O, no DI; consumers (the standalone verb
/// <c>polyphony pr validate-plan-diff</c> and the merge guard inside
/// <c>polyphony pr merge-plan-pr</c>) handle data fetching and emit the
/// JSON result.
///
/// <para>Path conventions (all repo-relative, forward-slash):</para>
/// <list type="bullet">
///   <item><c>plans/plan-{id}.md</c> — plan file for work item <c>{id}</c>.</item>
///   <item><c>.polyphony/**</c> — polyphony state (run manifest, locks, audit). Always blocked.</item>
///   <item>Anything else — implementation/code/doc files, treated as OK.</item>
/// </list>
///
/// <para>Severity rules (resolved in priority order — first match wins):</para>
/// <list type="number">
///   <item>Any path under <c>.polyphony/**</c> → <see cref="ValidationSeverity.Blocking"/>,
///     code <c>child_touched_polyphony_state</c>.</item>
///   <item>Any path matching an ancestor's plan file (above parent) →
///     <see cref="ValidationSeverity.Blocking"/>, code <c>child_touched_ancestor_plan</c>.</item>
///   <item>Path matches parent plan file AND <c>requestsParentChange == false</c> →
///     <see cref="ValidationSeverity.Blocking"/>, code <c>child_touched_parent_plan</c>.</item>
///   <item>Path matches parent plan file AND front-matter status is
///     <see cref="FrontMatterStatus.Malformed"/> →
///     <see cref="ValidationSeverity.Blocking"/>, code <c>malformed_front_matter</c>.</item>
///   <item>Path matches parent plan file AND front-matter status is
///     <see cref="FrontMatterStatus.Absent"/> →
///     <see cref="ValidationSeverity.Blocking"/>, code <c>missing_front_matter</c>.</item>
///   <item><c>requestsParentChange == true</c> AND no parent plan file touched →
///     <see cref="ValidationSeverity.Warning"/>, code <c>flag_set_no_parent_changes</c>.</item>
///   <item>Otherwise → <see cref="ValidationSeverity.None"/>, code <c>ok</c>.</item>
/// </list>
///
/// <para>Self plan file changes (the item's OWN plan) are always allowed;
/// they're the entire point of the PR.</para>
///
/// <para>Empty <paramref name="changedPaths"/> → severity <c>None</c> with
/// every bucket empty (degenerate but valid).</para>
/// </summary>
public static class PlanDiffValidator
{
    /// <summary>
    /// Result of a diff classification. <see cref="Severity"/> drives the
    /// caller's flow; <see cref="Code"/> is the stable machine-readable
    /// identifier; <see cref="Message"/> is a human-readable explanation
    /// suitable for embedding in a PR comment or workflow log. The
    /// per-bucket file lists are populated regardless of severity so the
    /// caller can render a structured report.
    /// </summary>
    public sealed record Result(
        ValidationSeverity Severity,
        string Code,
        string Message,
        IReadOnlyList<string> SelfPlanFiles,
        IReadOnlyList<string> ParentPlanFiles,
        IReadOnlyList<string> AncestorPlanFiles,
        IReadOnlyList<string> PolyphonyStateFiles,
        IReadOnlyList<string> OtherFiles);

    /// <summary>
    /// Classifies <paramref name="changedPaths"/> per the rules in the
    /// type-level docs.
    /// </summary>
    /// <param name="changedPaths">Repo-relative, forward-slash paths from <c>gh pr view --json files</c>.</param>
    /// <param name="selfPlanFile">Plan file for the PR's own item, e.g. <c>plans/plan-1234.md</c>.</param>
    /// <param name="parentPlanFile">Plan file for the immediate parent. <c>null</c> when the PR is for the root plan.</param>
    /// <param name="ancestorPlanFiles">Plan files for ancestors ABOVE the parent (grandparent and up; includes root's plan when applicable). Empty when self is root or when self's parent is root.</param>
    /// <param name="requestsParentChange">Value of the front-matter flag (false when absent/malformed).</param>
    /// <param name="frontMatterStatus">Outcome of strict front-matter parsing.</param>
    public static Result Check(
        IReadOnlyList<string> changedPaths,
        string selfPlanFile,
        string? parentPlanFile,
        IReadOnlyList<string> ancestorPlanFiles,
        bool requestsParentChange,
        FrontMatterStatus frontMatterStatus)
    {
        // SKELETON — real implementation lands in sdlc/p8b-validator-impl.
        // Returning a clean OK keeps existing MergePlanPr tests green
        // (validator-guard is a pass-through) until the agent's PR merges
        // and replaces this body. Tests for the validator itself live in
        // that PR.
        return new Result(
            ValidationSeverity.None,
            "ok",
            "Skeleton: no classification performed.",
            SelfPlanFiles: Array.Empty<string>(),
            ParentPlanFiles: Array.Empty<string>(),
            AncestorPlanFiles: Array.Empty<string>(),
            PolyphonyStateFiles: Array.Empty<string>(),
            OtherFiles: changedPaths.ToArray());
    }
}

/// <summary>Severity tier used by <see cref="PlanDiffValidator.Result"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>No issues; safe to merge.</summary>
    None,
    /// <summary>Advisory; merge allowed but the caller should surface the message.</summary>
    Warning,
    /// <summary>Hard block; merge must be refused.</summary>
    Blocking,
}

/// <summary>Outcome of strict plan-PR front-matter parsing.</summary>
public enum FrontMatterStatus
{
    /// <summary>Front-matter fence found and YAML parsed cleanly.</summary>
    Present,
    /// <summary>No front-matter fence at the start of the body.</summary>
    Absent,
    /// <summary>Fence found but YAML failed to parse, or required keys had wrong types.</summary>
    Malformed,
}
