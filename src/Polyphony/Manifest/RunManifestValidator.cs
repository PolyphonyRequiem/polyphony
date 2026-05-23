using Polyphony.Branching;
using Polyphony.Journal;

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
    /// The current manifest schema version emitted by new writes
    /// (post-W2). Schema 1 manifests are still loadable (legacy);
    /// schema 2 adds the <see cref="RunManifest.RunId"/> field.
    /// </summary>
    public const int CurrentSchema = 2;

    /// <summary>
    /// Lowest schema version the loader will accept. Anything below
    /// this is a hard error (no migration path).
    /// </summary>
    public const int MinSupportedSchema = 1;

    /// <summary>
    /// Highest schema version the loader will accept (alias for
    /// <see cref="CurrentSchema"/>). Kept distinct so a future read-only
    /// transition window between two schema versions is expressible
    /// without renaming.
    /// </summary>
    public const int MaxSupportedSchema = CurrentSchema;

    /// <summary>
    /// Back-compat alias for the value most callers historically read.
    /// Prefer <see cref="CurrentSchema"/> for writes and the
    /// <c>Min</c>/<c>Max</c> constants for range checks.
    /// </summary>
    public const int SupportedSchema = CurrentSchema;

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

        if (manifest.Schema < MinSupportedSchema || manifest.Schema > MaxSupportedSchema)
        {
            issues.Add($"schema must be in [{MinSupportedSchema},{MaxSupportedSchema}] (got {manifest.Schema}).");
        }

        if (manifest.BranchModelVersion != SupportedBranchModelVersion)
        {
            issues.Add($"branch_model_version must be {SupportedBranchModelVersion} (got {manifest.BranchModelVersion}).");
        }

        if (manifest.RootId <= 0)
        {
            issues.Add($"root_id must be positive (got {manifest.RootId}).");
        }

        if (manifest.RunId is not null && !RunIdMint.IsWellFormed(manifest.RunId))
        {
            issues.Add($"run_id '{manifest.RunId}' is not a 26-character Crockford-base32 ULID.");
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
        ValidateMergedPlanPrs(manifest.MergedPlanPrs, issues);

        return issues;
    }

    /// <summary>
    /// Returns non-fatal warnings (e.g., legacy schema 1 missing a
    /// <see cref="RunManifest.RunId"/>) that should be surfaced to
    /// operators without blocking the load. Always pair with
    /// <see cref="Validate"/>: warnings DO NOT subsume issues.
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(RunManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var warnings = new List<string>();
        if (manifest.Schema >= 2 && string.IsNullOrEmpty(manifest.RunId))
        {
            warnings.Add($"schema {manifest.Schema} manifest is missing run_id; new writers should populate it (W2).");
        }
        else if (manifest.Schema < 2 && string.IsNullOrEmpty(manifest.RunId))
        {
            warnings.Add("legacy schema 1 manifest predates the run_id primitive (W2); resume across processes is best-effort until a fresh init runs.");
        }

        return warnings;
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

    private static void ValidateMergedPlanPrs(List<MergedPlanPrEntry> merged, List<string> issues)
    {
        var seenPrNumbers = new HashSet<int>();

        for (var i = 0; i < merged.Count; i++)
        {
            var entry = merged[i];
            var prefix = $"merged_plan_prs[{i}]";

            if (entry.PrNumber <= 0)
            {
                issues.Add($"{prefix}.pr_number must be positive (got {entry.PrNumber}).");
            }
            else if (!seenPrNumbers.Add(entry.PrNumber))
            {
                issues.Add($"{prefix}.pr_number {entry.PrNumber} is a duplicate (each PR may appear at most once).");
            }

            if (string.IsNullOrWhiteSpace(entry.ItemKey))
            {
                issues.Add($"{prefix}.item_key must be non-empty.");
            }
            else if (entry.ItemKey != "root" && !int.TryParse(entry.ItemKey, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                issues.Add($"{prefix}.item_key '{entry.ItemKey}' must be 'root' or a numeric work-item id.");
            }

            if (string.IsNullOrWhiteSpace(entry.MergeCommit))
            {
                issues.Add($"{prefix}.merge_commit must be non-empty.");
            }

            if (entry.PreviousGeneration < 0)
            {
                issues.Add($"{prefix}.previous_generation must be >= 0 (got {entry.PreviousGeneration}).");
            }

            if (entry.CurrentGeneration <= entry.PreviousGeneration)
            {
                issues.Add($"{prefix}.current_generation must be > previous_generation (previous={entry.PreviousGeneration}, current={entry.CurrentGeneration}).");
            }

            if (entry.RecordedAt == default)
            {
                issues.Add($"{prefix}.recorded_at must be a real timestamp.");
            }
        }
    }
}
