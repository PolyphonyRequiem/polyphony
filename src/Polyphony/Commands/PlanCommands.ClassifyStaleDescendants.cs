using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Sdlc.Observers;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan classify-stale-descendants</c> — walks the descendant
/// tree of a root, finds open plan PRs, and classifies each by whether
/// its <c>ancestor_plan_generations</c> snapshot is behind the current
/// manifest's <see cref="RunManifest.PlanGenerations"/>.
///
/// <para>P9 (ancestor cascade): the workflow consumes this list to drive
/// the per-PR remedy step (auto-rebase / human-gate / recreate). This
/// verb is strictly classification — it does NOT modify any branches
/// or PRs.</para>
///
/// <para>Always exits 0 (routing-style verb). Workflow branches on the
/// presence of <see cref="PlanClassifyStaleDescendantsResult.Error"/>.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Classify open plan PRs under <paramref name="rootId"/>'s descendant
    /// tree by ancestor-snapshot staleness.
    /// </summary>
    /// <param name="rootId">Root work item ID — defines feature/{root} and is the manifest's owner.</param>
    /// <param name="manifestPath">Optional override of the run manifest path. When empty (default), derived under the git common dir via <c>PolyphonyStatePaths</c>. Pass an explicit path only as a testing seam.</param>
    /// <param name="platform">Optional platform override (<c>github</c>|<c>ado</c>); defaults to origin URL detection.</param>
    /// <param name="organization">ADO organization (required when <paramref name="platform"/> is <c>ado</c>).</param>
    /// <param name="project">ADO project (required when <paramref name="platform"/> is <c>ado</c>).</param>
    /// <param name="repositoryOverride">Repository override (GitHub <c>owner/repo</c> or ADO repo name).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("classify-stale-descendants")]
    [VerbResult(typeof(PlanClassifyStaleDescendantsResult))]
    public async Task<int> ClassifyStaleDescendants(
        int rootId = RequiredInput.MissingInt,
        string manifestPath = "",
        string platform = "",
        string organization = "",
        string project = "",
        string repositoryOverride = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan classify-stale-descendants",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate root id. ──────────────────────────────────────────
        if (!RootId.TryParse(rootId, out var root))
        {
            EmitClassifyError(rootId, manifestPath, $"rootId must be positive (got {rootId})", "invalid_root_id");
            return ExitCodes.Success;
        }

        // ── 2. Resolve repo identity (cross-platform). ────────────────────
        var resolved = await repoIdentityResolver.ResolveAsync(
            platform, organization, project, repositoryOverride, ct).ConfigureAwait(false);
        if (resolved.Identity is null)
        {
            EmitClassifyError(rootId, manifestPath,
                resolved.Error ?? "Could not resolve repo identity from origin remote", "no_slug");
            return ExitCodes.Success;
        }
        var identity = resolved.Identity;

        // ── 3. Read manifest from local common-dir state. ─────────────────
        // Rev 4.2: manifest is local + canonical; no git transaction.
        var featureBranch = BranchNameBuilder.Feature(root).Value;
        var resolvedPath = await ManifestPathHelper.ResolveAsync(statePaths, rootId, manifestPath, ct).ConfigureAwait(false);
        if (resolvedPath.Error is not null)
        {
            EmitClassifyError(rootId, manifestPath, resolvedPath.Error, "manifest_path_resolution_failed");
            return ExitCodes.Success;
        }
        var localManifestPath = resolvedPath.Path;

        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(localManifestPath);
        }
        catch (FileNotFoundException)
        {
            EmitClassifyError(rootId, localManifestPath,
                $"manifest not found at {localManifestPath}", "manifest_not_found");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitClassifyError(rootId, localManifestPath,
                $"manifest invalid: {ex.Message}", "manifest_invalid");
            return ExitCodes.Success;
        }

        if (manifest.RootId != rootId)
        {
            EmitClassifyError(rootId, localManifestPath,
                $"manifest at {localManifestPath} declares root {manifest.RootId}, expected {rootId}",
                "root_id_mismatch");
            return ExitCodes.Success;
        }

        // ── 4. Walk descendants (BFS, dedup, ordered by id ascending). ───
        var (descendantIds, parentOf) = await WalkDescendantIdsAsync(rootId, ct).ConfigureAwait(false);

        // ── 5. Per descendant: list open plan PRs, parse snapshot,
        // compare to manifest. Best-effort per item — failures degrade
        // to warnings and skip.
        var stale = new List<StalePlanPrDescendant>();
        var warnings = new List<string>();
        var withOpenPrs = 0;

        foreach (var descendantId in descendantIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!WorkItemId.TryParse(descendantId, out var item)) continue;

            var planBranch = BranchNameBuilder.DescendantPlan(root, item).Value;

            IReadOnlyList<PullRequestSummary> prs;
            try
            {
                prs = await pullRequestReader.ListByHeadAsync(
                    identity, planBranch, "open", limit: 10, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"item {descendantId}: gh pr list failed ({ex.Message}); skipped");
                continue;
            }

            if (prs.Count == 0) continue;
            withOpenPrs++;

            // Take the highest-numbered open PR; older open PRs on the
            // same head are ignored (matches detect-state semantics).
            var pr = prs.OrderByDescending(p => p.Number).First();

            GhPullRequestPollData? poll;
            try
            {
                poll = await pullRequestReader.GetPollDataAsync(identity, pr.Number, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"item {descendantId} PR #{pr.Number}: gh pr view failed ({ex.Message}); skipped");
                continue;
            }
            if (poll is null) continue;

            var staleAncestors = ClassifyAgainstManifest(
                poll.Body ?? string.Empty,
                manifest,
                rootId);
            if (staleAncestors.Count == 0) continue;

            stale.Add(new StalePlanPrDescendant
            {
                ItemId = descendantId,
                ParentItemId = parentOf.TryGetValue(descendantId, out var parentId) ? parentId : rootId,
                PrNumber = pr.Number,
                PrUrl = pr.Url ?? string.Empty,
                HeadRef = poll.HeadRefName ?? planBranch,
                HeadSha = poll.HeadRefOid,
                StaleAncestors = staleAncestors,
            });
        }

        EmitClassify(new PlanClassifyStaleDescendantsResult
        {
            RootId = rootId,
            ManifestRef = string.Empty,
            ManifestPath = localManifestPath,
            TotalDescendantsScanned = descendantIds.Count,
            TotalDescendantsWithOpenPrs = withOpenPrs,
            TotalStale = stale.Count,
            StaleDescendants = stale,
            Warnings = warnings,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// BFS-walk descendants of <paramref name="rootId"/> via the work-item
    /// repository. Excludes the root itself. Dedups defensively. Returns
    /// ids in BFS order (depth-major, ids ascending within a wave) plus a
    /// parent-of map (descendant id → immediate parent id, including the
    /// root for direct children).
    /// </summary>
    private async Task<(IReadOnlyList<int> Ordered, IReadOnlyDictionary<int, int> ParentOf)> WalkDescendantIdsAsync(int rootId, CancellationToken ct)
    {
        var seen = new HashSet<int> { rootId };
        var ordered = new List<int>();
        var parentOf = new Dictionary<int, int>();
        var frontier = new List<int> { rootId };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<int>();
            foreach (var parentId in frontier)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<Twig.Domain.Aggregates.WorkItem> children;
                try
                {
                    children = await repository.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Missing twig cache entry is best-effort: the node
                    // simply has no known children for the walk.
                    continue;
                }

                foreach (var child in children.OrderBy(c => c.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    ordered.Add(child.Id);
                    parentOf[child.Id] = parentId;
                    nextFrontier.Add(child.Id);
                }
            }
            frontier = nextFrontier;
        }

        return (ordered, parentOf);
    }

    /// <summary>
    /// Compare a plan PR body's <c>ancestor_plan_generations</c> snapshot
    /// against the manifest. Returns one entry per ancestor whose
    /// manifest generation is strictly greater than the snapshot. Empty
    /// list means "snapshot is current — no remedy needed".
    /// </summary>
    private static IReadOnlyList<StaleAncestorEntry> ClassifyAgainstManifest(
        string prBody,
        RunManifest manifest,
        int rootId)
    {
        var snapshot = PlanPrFrontMatter.Parse(prBody).AncestorPlanGenerations;
        if (snapshot.Count == 0) return [];

        var stale = new List<StaleAncestorEntry>();
        foreach (var (key, snapshotGen) in snapshot.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!manifest.PlanGenerations.TryGetValue(key, out var currentGen)) continue;
            if (currentGen <= snapshotGen) continue;

            stale.Add(new StaleAncestorEntry
            {
                AncestorKey = key,
                SnapshotGeneration = snapshotGen,
                CurrentGeneration = currentGen,
            });
        }
        return stale;
    }

    private static void EmitClassify(PlanClassifyStaleDescendantsResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanClassifyStaleDescendantsResult));

    private static void EmitClassifyError(
        int rootId,
        string manifestPath,
        string error,
        string errorCode,
        string manifestRef = "")
        => EmitClassify(new PlanClassifyStaleDescendantsResult
        {
            RootId = rootId,
            ManifestRef = manifestRef,
            ManifestPath = manifestPath,
            Error = error,
            ErrorCode = errorCode,
        });
}
