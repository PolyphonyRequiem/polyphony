namespace Polyphony.Branching;

/// <summary>
/// Discriminated union of every recognized Polyphony branch shape under the
/// Rev 4 grammar, plus an <see cref="Unrecognized"/> case for git refs that
/// fall outside it (e.g. <c>main</c>, <c>release/v1</c>, ad-hoc dev branches).
/// Use exhaustive <c>switch</c> patterns at consumers so the compiler flags
/// missed cases when the grammar evolves.
/// </summary>
/// <remarks>
/// The union is intentionally closed: the private constructor prevents
/// external types from inheriting <see cref="ParsedBranch"/>, so a
/// <c>switch</c> over the seven sealed records below is exhaustive. The
/// base type intentionally exposes no abstract <c>Branch</c> or
/// <c>RootId</c> property — that would either force an <c>init</c> setter
/// (clashing with the positional records) or invite anemic-base inheritance.
/// Use pattern matching at the call site instead; the compiler will flag
/// missed cases when a new variant is added.
/// </remarks>
internal abstract record ParsedBranch
{
    private ParsedBranch()
    {
    }

    /// <summary>
    /// <c>feature/{root_id}</c> — the apex integration trunk for a run.
    /// </summary>
    public sealed record Feature(BranchName Branch, RootId RootId) : ParsedBranch;

    /// <summary>
    /// <c>plan/{root_id}</c> — the root plan branch for a run.
    /// </summary>
    public sealed record RootPlan(BranchName Branch, RootId RootId) : ParsedBranch;

    /// <summary>
    /// <c>plan/{root_id}-{item_id}</c> — a descendant plan branch (flat;
    /// hierarchy is captured by the PR's base branch, not the name).
    /// </summary>
    public sealed record DescendantPlan(BranchName Branch, RootId RootId, WorkItemId ItemId) : ParsedBranch;

    /// <summary>
    /// <c>mg/{root_id}_{mg_path}</c> — a merge group at any depth. The
    /// <c>Path</c> carries depth via segment count; top-level paths
    /// have <c>Path.IsTopLevel == true</c>.
    /// </summary>
    public sealed record MergeGroup(BranchName Branch, RootId RootId, MergeGroupPath Path) : ParsedBranch;

    /// <summary>
    /// <c>impl/{root_id}-{item_id}</c> — an impl branch (flat; PR base
    /// records the enclosing MG).
    /// </summary>
    public sealed record Impl(BranchName Branch, RootId RootId, WorkItemId ItemId) : ParsedBranch;

    /// <summary>
    /// <c>evidence/{root_id}-{item_id}</c> — an evidence branch (Phase 6).
    /// </summary>
    public sealed record Evidence(BranchName Branch, RootId RootId, WorkItemId ItemId) : ParsedBranch;

    /// <summary>
    /// A git ref that is syntactically outside the Rev 4 Polyphony grammar.
    /// The raw string is preserved so callers can log/route it; no
    /// <see cref="RootId"/> can be inferred.
    /// </summary>
    public sealed record Unrecognized(string Raw) : ParsedBranch;
}

