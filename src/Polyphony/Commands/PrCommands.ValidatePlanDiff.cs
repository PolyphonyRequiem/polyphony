using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Phase 3 P8b: classify a plan PR's changed-file set against the
    /// plan-tree taxonomy and emit a structured result. Pure read — no
    /// platform merge, no manifest mutation, no lock acquisition.
    ///
    /// <para><b>Two consumers, one classifier.</b> This verb is the
    /// standalone advisory pass invoked by <c>plan-level.yaml</c>
    /// (e.g. as a status-check or pre-merge gate). The same
    /// <see cref="PlanDiffValidator"/> helper runs atomically inside
    /// <c>polyphony pr merge-plan-pr</c> so the merge-time guard cannot
    /// drift from the advisory verdict — only timing differs.</para>
    ///
    /// <para><b>Severity routing.</b> Workflow consumers branch on
    /// <see cref="PrValidatePlanDiffResult.Severity"/>:</para>
    /// <list type="bullet">
    ///   <item><c>none</c> — clean.</item>
    ///   <item><c>warning</c> — advisory; merge allowed.</item>
    ///   <item><c>blocking</c> — refuse the merge.</item>
    ///   <item><c>error</c> — verb failed before classification (PR not
    ///     found, gh hung, slug unresolved). Distinct from <c>blocking</c>
    ///     so workflows can route the two outcomes differently (retry vs
    ///     human gate).</item>
    /// </list>
    ///
    /// <para><b>Routing-style exit code.</b> Always exits 0; consumers
    /// route on <see cref="PrValidatePlanDiffResult.Severity"/> +
    /// <see cref="PrValidatePlanDiffResult.Code"/>. Exits non-zero only
    /// on truly unexpected exceptions.</para>
    /// </summary>
    /// <param name="rootId">Run's root work-item id.</param>
    /// <param name="itemId">Plan-owning work-item id; equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="prNumber">PR number to validate.</param>
    /// <param name="parentItemId">Immediate plan-tree parent id; 0 when item is root or a direct child of root.</param>
    /// <param name="ancestorIds">Comma-separated ancestor ids ABOVE the immediate parent, ending in the literal token <c>root</c>. Empty when item is root or a direct child of root. Format matches the <c>ancestor_ids</c> field emitted by <c>plan derive-ancestor-chain</c> minus the leading parent id.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("validate-plan-diff")]
    [VerbResult(typeof(PrValidatePlanDiffResult))]
    public async Task<int> ValidatePlanDiff(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int prNumber = RequiredInput.MissingInt,
        int parentItemId = 0,
        string ancestorIds = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr validate-plan-diff",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate inputs. ─────────────────────────────────────────
        if (rootId <= 0)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "config_error", $"--root-id must be positive (got {rootId})");

        if (itemId <= 0)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "config_error", $"--item-id must be positive (got {itemId})");

        if (prNumber <= 0)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "config_error", $"--pr-number must be positive (got {prNumber})");

        bool isRootPlan = itemId == rootId;

        if (isRootPlan && parentItemId != 0)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "config_error",
                $"--parent-item-id must be omitted when --item-id == --root-id (got {parentItemId}); the root plan has no parent.");

        if (!isRootPlan && parentItemId < 0)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "config_error", $"--parent-item-id must be non-negative (got {parentItemId})");

        // ── 2. Resolve repo slug. ───────────────────────────────────────
        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "repo_not_resolved",
                "Could not resolve repo slug from origin remote.");

        // ── 3. Poll PR for body + state + head SHA. ─────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "internal_error", $"Could not poll PR #{prNumber}: {ex.Message}",
                slug: slug);
        }

        if (poll is null)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "pr_not_found", $"PR #{prNumber} not found in repo {slug}.",
                slug: slug);

        // ── 4. Fetch changed files. ─────────────────────────────────────
        IReadOnlyList<GhPullRequestChangedFile>? files;
        try
        {
            files = await gh.GetPullRequestFilesAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "internal_error", $"Could not fetch PR #{prNumber} files: {ex.Message}",
                slug: slug, headSha: poll.HeadRefOid ?? "", prState: poll.State);
        }

        if (files is null)
            return EmitValidateError(rootId, itemId, parentItemId, prNumber,
                "pr_not_found", $"PR #{prNumber} files endpoint returned no payload.",
                slug: slug, headSha: poll.HeadRefOid ?? "", prState: poll.State);

        // ── 5. Parse front-matter strictly + compute file paths. ────────
        var frontMatter = PlanPrFrontMatter.ParseStrict(poll.Body);
        var changedPaths = files.Select(f => f.Path).ToList();
        var selfPlanFile = PlanFilePath(itemId);
        string? parentPlanFile = (isRootPlan || parentItemId == 0) ? null : PlanFilePath(parentItemId);
        var ancestorPlanFiles = ParseAncestorIds(ancestorIds, rootId, parentItemId)
            .Select(PlanFilePath)
            .ToList();

        // ── 6. Classify. ────────────────────────────────────────────────
        var classification = PlanDiffValidator.Check(
            changedPaths,
            selfPlanFile,
            parentPlanFile,
            ancestorPlanFiles,
            frontMatter.RequestsParentChange,
            frontMatter.Status);

        // ── 7. Emit. ────────────────────────────────────────────────────
        EmitValidate(new PrValidatePlanDiffResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            PrNumber = prNumber,
            RepoSlug = slug,
            HeadSha = poll.HeadRefOid ?? "",
            PrState = poll.State,
            Severity = SeverityToken(classification.Severity),
            Code = classification.Code,
            Message = classification.Message,
            RequestsParentChange = frontMatter.RequestsParentChange,
            DiffClassified = true,
            SelfPlanFiles = classification.SelfPlanFiles,
            ParentPlanFiles = classification.ParentPlanFiles,
            AncestorPlanFiles = classification.AncestorPlanFiles,
            PolyphonyStateFiles = classification.PolyphonyStateFiles,
            OtherFiles = classification.OtherFiles,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Compute the conventional plan file path for a work item. The
    /// convention <c>plans/plan-{id}.md</c> is the same for descendants
    /// AND the root (the manifest uses the literal key <c>"root"</c>,
    /// but the plan FILE is always named by the numeric id).
    /// </summary>
    internal static string PlanFilePath(int workItemId)
        => $"plans/plan-{workItemId.ToString(CultureInfo.InvariantCulture)}.md";

    /// <summary>
    /// Decompose the <c>--ancestor-ids</c> CLI flag into the list of
    /// integer ancestor ids ABOVE the immediate parent. The format
    /// matches what <c>plan derive-ancestor-chain</c> emits in its
    /// <c>ancestor_ids</c> field — a comma-separated list of integer ids
    /// terminating in the literal token <c>root</c> (which is replaced
    /// with the actual <paramref name="rootId"/>). The immediate parent
    /// is excluded because it's passed via <c>--parent-item-id</c>.
    ///
    /// <para>Examples:</para>
    /// <list type="bullet">
    ///   <item><c>""</c> → <c>[]</c> (root plan or direct child of root).</item>
    ///   <item><c>"root"</c> → <c>[rootId]</c> (item is a grandchild; parent is also between item and root, but already filtered out by caller).</item>
    ///   <item><c>"500,root"</c> → <c>[500, rootId]</c>.</item>
    /// </list>
    ///
    /// <para>Filters: drops the immediate parent id if it accidentally
    /// appears (defensive — caller is supposed to exclude it). Drops
    /// duplicates. Ignores empty/whitespace tokens.</para>
    /// </summary>
    internal static IReadOnlyList<int> ParseAncestorIds(string ancestorIds, int rootId, int parentItemId)
    {
        if (string.IsNullOrWhiteSpace(ancestorIds))
            return Array.Empty<int>();

        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (var raw in ancestorIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int id;
            if (string.Equals(raw, "root", StringComparison.OrdinalIgnoreCase))
            {
                id = rootId;
            }
            else if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                continue;
            }

            if (id <= 0) continue;
            if (id == parentItemId) continue;
            if (!seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }

    private static string SeverityToken(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.None => "none",
        ValidationSeverity.Warning => "warning",
        ValidationSeverity.Blocking => "blocking",
        _ => "none",
    };

    private static void EmitValidate(PrValidatePlanDiffResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrValidatePlanDiffResult));

    private static int EmitValidateError(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        string code,
        string message,
        string slug = "",
        string headSha = "",
        string prState = "")
    {
        EmitValidate(new PrValidatePlanDiffResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            PrNumber = prNumber,
            RepoSlug = slug,
            HeadSha = headSha,
            PrState = prState,
            Severity = "error",
            Code = code,
            Message = message,
            RequestsParentChange = false,
            DiffClassified = false,
            Error = message,
        });
        return ExitCodes.Success;
    }
}
