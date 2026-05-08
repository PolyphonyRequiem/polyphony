using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;

namespace Polyphony.Commands;

/// <summary>
/// Merge-group-level verbs: nesting-decision (and future verbs for run
/// manifest, topology hash, retirement). Pure decision verbs that take
/// fully-resolved inputs from the workflow and return a JSON decision —
/// they do not read from the work-item store, so the workflow remains
/// the single owner of "what does this item look like right now".
///
/// These verbs ALWAYS exit 0 on a valid decision (success / nest / flat
/// are all "success"); validation errors return ConfigError per the
/// project-wide convention.
/// </summary>
[VerbGroup("mg")]
public sealed partial class MgCommands
{
    /// <summary>
    /// Decide whether a child item under an enclosing merge group becomes
    /// its own nested MG (<c>nest</c>) or stays flat as a impl PR
    /// (<c>flat</c>). Implements ADR <c>docs/decisions/branch-model.md</c>
    /// § Nested-MG trigger: default-nest when the child is
    /// <c>implementable AND decomposable</c>. Planner overrides
    /// (<c>--override-flat</c> and <c>--override-nested-mg-id</c>) are
    /// mutually exclusive on a given child; passing both is a config
    /// error.
    /// </summary>
    /// <param name="rootId">Run's apex (focus) work-item id (positive).</param>
    /// <param name="itemId">Child work-item id being decided about (positive).</param>
    /// <param name="parentMgPath">
    /// Canonical <c>_</c>-joined merge-group path of the enclosing MG.
    /// Required — the verb does not handle root-level placement (that is
    /// the parent planner's job, encoded in <c>children_overrides</c>).
    /// </param>
    /// <param name="hasImplementable">
    /// Whether the child carries the <c>implementable</c> facet. The
    /// caller (workflow) extracts this from the planner-emitted child
    /// metadata.
    /// </param>
    /// <param name="decomposable">
    /// Whether the child is <c>decomposable: true</c>. The caller
    /// (workflow) extracts this from the planner-emitted child metadata.
    /// </param>
    /// <param name="overrideFlat">
    /// Planner override: force the child to flat regardless of the
    /// trigger. Mutually exclusive with <paramref name="overrideNestedMgId"/>.
    /// </param>
    /// <param name="overrideNestedMgId">
    /// Planner override: name the nested MG explicitly. Must satisfy
    /// <see cref="MergeGroupId.GrammarPattern"/>. Mutually exclusive
    /// with <paramref name="overrideFlat"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("nesting-decision")]
    [VerbResult(typeof(MgNestingDecisionResult))]
    public Task<int> NestingDecision(
        int rootId,
        int itemId,
        string parentMgPath,
        bool hasImplementable,
        bool decomposable,
        bool overrideFlat = false,
        string overrideNestedMgId = "",
        CancellationToken ct = default)
    {
        _ = ct; // pure function — no async work needed yet.

        if (!Branching.RootId.TryParse(rootId, out var typedRootId))
        {
            EmitError(rootId, itemId, parentMgPath, hasImplementable, decomposable, $"rootId must be positive (got {rootId})");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (!WorkItemId.TryParse(itemId, out var typedItemId))
        {
            EmitError(rootId, itemId, parentMgPath, hasImplementable, decomposable, $"itemId must be positive (got {itemId})");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (!MergeGroupPath.TryParse(parentMgPath, out var parentPath) || parentPath is null)
        {
            EmitError(
                rootId,
                itemId,
                parentMgPath,
                hasImplementable,
                decomposable,
                $"'{parentMgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (overrideFlat && !string.IsNullOrEmpty(overrideNestedMgId))
        {
            EmitError(
                rootId,
                itemId,
                parentMgPath,
                hasImplementable,
                decomposable,
                "--override-flat and --override-nested-mg-id are mutually exclusive (per ADR § Override).");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (overrideFlat)
        {
            EmitFlat(typedRootId, typedItemId, parentPath, hasImplementable, decomposable,
                overrideApplied: "flat",
                reason: "planner override: --override-flat forces impl PR even when default trigger would nest");
            return Task.FromResult(ExitCodes.Success);
        }

        if (!string.IsNullOrEmpty(overrideNestedMgId))
        {
            if (!MergeGroupId.TryParse(overrideNestedMgId, out var nestedId))
            {
                EmitError(
                    rootId,
                    itemId,
                    parentMgPath,
                    hasImplementable,
                    decomposable,
                    $"--override-nested-mg-id '{overrideNestedMgId}' is not a valid merge-group id. Grammar: {MergeGroupId.GrammarPattern}.");
                return Task.FromResult(ExitCodes.ConfigError);
            }

            EmitNest(typedRootId, typedItemId, parentPath, nestedId, hasImplementable, decomposable,
                overrideApplied: "nested-mg-id",
                reason: $"planner override: explicit nested mg id '{nestedId.Value}'");
            return Task.FromResult(ExitCodes.Success);
        }

        // Default trigger: nest iff (decomposable AND has_implementable).
        if (decomposable && hasImplementable)
        {
            // Derive nested id from item id. The literal 'item-' prefix is
            // mandatory so the derived form starts with a lowercase letter
            // and is unambiguous in branch listings (per ADR § Nested MG
            // id - source rule).
            var derived = MergeGroupId.Parse($"item-{itemId}");
            EmitNest(typedRootId, typedItemId, parentPath, derived, hasImplementable, decomposable,
                overrideApplied: "default",
                reason: $"default-nest trigger: decomposable AND implementable -> nest with derived id '{derived.Value}'");
            return Task.FromResult(ExitCodes.Success);
        }

        var triggerReason = (decomposable, hasImplementable) switch
        {
            (false, false) => "default-nest trigger: not decomposable AND not implementable -> flat",
            (false, true) => "default-nest trigger: not decomposable -> flat (implementable alone is not enough)",
            (true, false) => "default-nest trigger: not implementable -> flat (decomposable alone is not enough)",
            _ => "default-nest trigger: flat",
        };
        EmitFlat(typedRootId, typedItemId, parentPath, hasImplementable, decomposable,
            overrideApplied: "default",
            reason: triggerReason);
        return Task.FromResult(ExitCodes.Success);
    }

    private static void EmitNest(
        RootId rootId,
        WorkItemId itemId,
        MergeGroupPath parentPath,
        MergeGroupId nestedId,
        bool hasImplementable,
        bool decomposable,
        string overrideApplied,
        string reason)
    {
        var nestedPath = parentPath.Push(nestedId);
        Emit(new MgNestingDecisionResult
        {
            RootId = rootId.Value,
            ItemId = itemId.Value,
            ParentMgPath = parentPath.Canonical,
            Decision = "nest",
            NestedMgId = nestedId.Value,
            NestedMgPath = nestedPath.Canonical,
            ImplBranch = null,
            HasImplementable = hasImplementable,
            Decomposable = decomposable,
            OverrideApplied = overrideApplied,
            Reason = reason,
        });
    }

    private static void EmitFlat(
        RootId rootId,
        WorkItemId itemId,
        MergeGroupPath parentPath,
        bool hasImplementable,
        bool decomposable,
        string overrideApplied,
        string reason)
    {
        Emit(new MgNestingDecisionResult
        {
            RootId = rootId.Value,
            ItemId = itemId.Value,
            ParentMgPath = parentPath.Canonical,
            Decision = "flat",
            NestedMgId = null,
            NestedMgPath = null,
            ImplBranch = BranchNameBuilder.Impl(rootId, itemId).Value,
            HasImplementable = hasImplementable,
            Decomposable = decomposable,
            OverrideApplied = overrideApplied,
            Reason = reason,
        });
    }

    private static void EmitError(
        int rootId,
        int itemId,
        string parentMgPath,
        bool hasImplementable,
        bool decomposable,
        string message)
    {
        Emit(new MgNestingDecisionResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentMgPath = parentMgPath,
            Decision = "error",
            NestedMgId = null,
            NestedMgPath = null,
            ImplBranch = null,
            HasImplementable = hasImplementable,
            Decomposable = decomposable,
            OverrideApplied = "",
            Reason = "validation failed",
            Error = message,
        });
    }

    private static void Emit(MgNestingDecisionResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.MgNestingDecisionResult));
}
