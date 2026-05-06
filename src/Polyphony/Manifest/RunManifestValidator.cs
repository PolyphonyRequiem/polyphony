using Polyphony.Branching;

namespace Polyphony.Manifest;

/// <summary>
/// Validates a <see cref="RunManifest"/> against the structural
/// invariants required by the Rev 4 branch-model ADR. Returns the full
/// list of issues so callers can render them all at once; CLI verbs
/// translate that into a JSON error envelope.
/// </summary>
public static class RunManifestValidator
{
    /// <summary>
    /// The supported manifest schema version.
    /// </summary>
    public const int SupportedSchema = 1;

    /// <summary>
    /// The supported branch-model version (matches Rev 4 of the ADR).
    /// </summary>
    public const int SupportedBranchModelVersion = 1;

    /// <summary>
    /// Validates the manifest. Returns an empty list when fully valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(RunManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var issues = new List<string>();

        if (manifest.Schema != SupportedSchema)
        {
            issues.Add($"schema must be {SupportedSchema} (got {manifest.Schema}).");
        }

        if (manifest.BranchModelVersion != SupportedBranchModelVersion)
        {
            issues.Add($"branch_model_version must be {SupportedBranchModelVersion} (got {manifest.BranchModelVersion}).");
        }

        if (manifest.RootId <= 0)
        {
            issues.Add($"root_id must be positive (got {manifest.RootId}).");
        }

        if (string.IsNullOrWhiteSpace(manifest.PlatformProject))
        {
            issues.Add("platform_project must be non-empty.");
        }

        if (manifest.CreatedAt == default)
        {
            issues.Add("created_at must be a real timestamp.");
        }

        if (string.IsNullOrWhiteSpace(manifest.CreatedBy))
        {
            issues.Add("created_by must be non-empty.");
        }

        ValidatePlanGenerations(manifest.PlanGenerations, issues);
        ValidateMergeGroups(manifest.MergeGroups, issues);
        ValidateRebases(manifest.Rebases, issues);
        ValidateApprovals(manifest.HumanApprovals, issues);
        ValidateRetired(manifest.RetiredMergeGroupIds, issues);

        return issues;
    }

    /// <summary>Throwing variant — used by the loader.</summary>
    public static void ValidateOrThrow(RunManifest manifest, string sourcePath = "<inline>")
    {
        var issues = Validate(manifest);
        if (issues.Count == 0)
        {
            return;
        }

        var summary = string.Join("; ", issues);
        throw new InvalidOperationException($"Invalid run manifest at {sourcePath}: {summary}");
    }

    private static void ValidatePlanGenerations(Dictionary<string, int> planGenerations, List<string> issues)
    {
        foreach (var (key, value) in planGenerations)
        {
            if (value < 0)
            {
                issues.Add($"plan_generations[{key}] must be >= 0 (got {value}).");
            }

            if (key != "root" && !int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                issues.Add($"plan_generations key '{key}' must be 'root' or a numeric work-item id.");
            }
        }
    }

    private static void ValidateMergeGroups(List<MergeGroupEntry> mergeGroups, List<string> issues)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < mergeGroups.Count; i++)
        {
            var entry = mergeGroups[i];
            var prefix = $"merge_groups[{i}]";

            if (!MergeGroupId.TryParse(entry.Id, out var typedId))
            {
                issues.Add($"{prefix}.id '{entry.Id}' violates the MG-id grammar {MergeGroupId.GrammarPattern}.");
                continue;
            }

            if (entry.Id == ManifestOverride.Flat)
            {
                issues.Add($"{prefix}.id '{entry.Id}' is the reserved sentinel '{ManifestOverride.Flat}' and may not be used as an id.");
            }

            if (!MergeGroupPath.TryParse(entry.MgPath, out var parsedPath) || parsedPath is null)
            {
                issues.Add($"{prefix}.mg_path '{entry.MgPath}' is not a valid '_'-joined merge-group path.");
                continue;
            }

            if (parsedPath.Terminal.Value != typedId.Value)
            {
                issues.Add($"{prefix}.id '{entry.Id}' must equal the terminal segment of mg_path '{entry.MgPath}' (got terminal '{parsedPath.Terminal.Value}').");
            }

            if (!seenPaths.Add(entry.MgPath))
            {
                issues.Add($"{prefix}.mg_path '{entry.MgPath}' is a duplicate (mg_path values must be unique).");
            }

            ValidateNestingConsistency(entry, parsedPath, prefix, issues);
            ValidateIsolation(entry, prefix, issues);
            ValidateOverride(entry, prefix, issues);
            ValidateItems(entry, prefix, issues);
        }
    }

    private static void ValidateNestingConsistency(
        MergeGroupEntry entry,
        MergeGroupPath parsedPath,
        string prefix,
        List<string> issues)
    {
        if (!ManifestNesting.ValidValues.Contains(entry.Nesting))
        {
            issues.Add($"{prefix}.nesting '{entry.Nesting}' must be 'top' or 'nested'.");
        }

        if (parsedPath.IsTopLevel)
        {
            if (entry.Nesting != ManifestNesting.Top)
            {
                issues.Add($"{prefix} has top-level mg_path '{entry.MgPath}' but nesting is '{entry.Nesting}' (expected 'top').");
            }

            if (entry.ParentMgPath is not null)
            {
                issues.Add($"{prefix} top-level entry must have parent_mg_path null (got '{entry.ParentMgPath}').");
            }
        }
        else
        {
            if (entry.Nesting != ManifestNesting.Nested)
            {
                issues.Add($"{prefix} has nested mg_path '{entry.MgPath}' but nesting is '{entry.Nesting}' (expected 'nested').");
            }

            if (string.IsNullOrEmpty(entry.ParentMgPath))
            {
                issues.Add($"{prefix} nested entry must have a non-empty parent_mg_path.");
                return;
            }

            // parent_mg_path must equal mg_path with the terminal segment removed.
            var expectedParent = string.Join('_', parsedPath.Segments.Take(parsedPath.Depth - 1).Select(s => s.Value));
            if (entry.ParentMgPath != expectedParent)
            {
                issues.Add($"{prefix}.parent_mg_path '{entry.ParentMgPath}' must equal mg_path with the terminal segment dropped (expected '{expectedParent}').");
            }
        }
    }

    private static void ValidateIsolation(MergeGroupEntry entry, string prefix, List<string> issues)
    {
        if (!ManifestIsolation.ValidValues.Contains(entry.Isolation))
        {
            issues.Add($"{prefix}.isolation '{entry.Isolation}' must be 'per-merge-group' or 'per-item'.");
        }
    }

    private static void ValidateOverride(MergeGroupEntry entry, string prefix, List<string> issues)
    {
        if (entry.NestingOverride is null)
        {
            return;
        }

        if (entry.NestingOverride == ManifestOverride.Flat)
        {
            return;
        }

        if (!MergeGroupId.TryParse(entry.NestingOverride, out _))
        {
            issues.Add($"{prefix}.nesting_override '{entry.NestingOverride}' must be null, '{ManifestOverride.Flat}', or a valid MG-id ({MergeGroupId.GrammarPattern}).");
        }
    }

    private static void ValidateItems(MergeGroupEntry entry, string prefix, List<string> issues)
    {
        for (var i = 0; i < entry.Items.Count; i++)
        {
            if (entry.Items[i] <= 0)
            {
                issues.Add($"{prefix}.items[{i}] must be positive (got {entry.Items[i]}).");
            }
        }
    }

    private static void ValidateRebases(List<RebaseRecord> rebases, List<string> issues)
    {
        for (var i = 0; i < rebases.Count; i++)
        {
            var r = rebases[i];
            var prefix = $"rebases[{i}]";
            if (string.IsNullOrWhiteSpace(r.Branch)) issues.Add($"{prefix}.branch must be non-empty.");
            if (string.IsNullOrWhiteSpace(r.Onto)) issues.Add($"{prefix}.onto must be non-empty.");
            if (string.IsNullOrWhiteSpace(r.Reason)) issues.Add($"{prefix}.reason must be non-empty.");
            if (string.IsNullOrWhiteSpace(r.Commit)) issues.Add($"{prefix}.commit must be non-empty.");
            if (r.RecordedAt == default) issues.Add($"{prefix}.recorded_at must be a real timestamp.");
        }
    }

    private static void ValidateApprovals(List<HumanApprovalRecord> approvals, List<string> issues)
    {
        for (var i = 0; i < approvals.Count; i++)
        {
            var a = approvals[i];
            var prefix = $"human_approvals[{i}]";
            if (string.IsNullOrWhiteSpace(a.Gate)) issues.Add($"{prefix}.gate must be non-empty.");
            if (string.IsNullOrWhiteSpace(a.ApprovedBy)) issues.Add($"{prefix}.approved_by must be non-empty.");
            if (a.ApprovedAt == default) issues.Add($"{prefix}.approved_at must be a real timestamp.");
        }
    }

    private static void ValidateRetired(List<RetiredMergeGroupRecord> retired, List<string> issues)
    {
        for (var i = 0; i < retired.Count; i++)
        {
            var r = retired[i];
            var prefix = $"retired_merge_group_ids[{i}]";
            if (!MergeGroupId.TryParse(r.Id, out _))
            {
                issues.Add($"{prefix}.id '{r.Id}' violates the MG-id grammar.");
            }
            if (r.RetiredAt == default) issues.Add($"{prefix}.retired_at must be a real timestamp.");
        }
    }
}
