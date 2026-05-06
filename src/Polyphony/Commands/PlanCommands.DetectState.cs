using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan detect-state</c> — inspect the live plan workflow state
/// for a single work item by composing observable signals: plan branch on
/// origin, latest plan PR, run-manifest plan-generation ledger, and the
/// parent's <c>polyphony:planned</c> tag.
///
/// <para>This is the routing primitive consumed by <c>plan-level.yaml</c>
/// at the top of every workflow run. It replaces the old "always run the
/// architect" entry behavior with "discover where we left off and re-enter
/// at the right step."</para>
///
/// <para>Always exits 0 (routing-style verb). Workflow branches on
/// <c>state</c> in the JSON payload.</para>
/// </summary>
public sealed partial class PlanCommands
{
    private const string PlannedTagDefault = "polyphony:planned";

    private static readonly System.Text.RegularExpressions.Regex GitHubSlugRegexDetect =
        new(@"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?/?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Detect the current plan-workflow state for a work item.
    /// </summary>
    /// <param name="rootId">Root work item ID (defines the feature/{root}
    /// integration branch and is the manifest's owner).</param>
    /// <param name="itemId">Work item the plan is for. Equal to <paramref name="rootId"/>
    /// for the root plan; otherwise a descendant.</param>
    /// <param name="manifestPath">Run manifest path within the
    /// <c>origin/feature/{root}</c> blob. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="plannedTag">Tag value applied by <c>plan seed-children</c>
    /// to mark the parent as seeded. Used to discriminate
    /// <c>merged_unseeded</c> from <c>complete</c>. Defaults to
    /// <c>polyphony:planned</c> — match the seeder's default.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("detect-state")]
    public async Task<int> DetectState(
        int rootId,
        int itemId,
        string manifestPath = ".polyphony/run.yaml",
        string plannedTag = PlannedTagDefault,
        CancellationToken ct = default)
    {
        // ── 1. Validate. ──────────────────────────────────────────────────
        if (!RootId.TryParse(rootId, out var root))
        {
            EmitDetectStateError(rootId, itemId, "", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitDetectStateError(rootId, itemId, "", $"itemId must be positive (got {itemId})");
            return ExitCodes.Success;
        }

        var isRootPlan = rootId == itemId;
        var planBranch = isRootPlan
            ? BranchNameBuilder.RootPlan(root).Value
            : BranchNameBuilder.DescendantPlan(root, item).Value;
        var featureBranch = BranchNameBuilder.Feature(root).Value;
        var manifestRef = $"origin/{featureBranch}";

        // ── 2. Slug. ──────────────────────────────────────────────────────
        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
        {
            EmitDetectStateError(rootId, itemId, planBranch,
                "Could not resolve repo slug from origin remote");
            return ExitCodes.Success;
        }

        // ── 3. Branch on origin? ──────────────────────────────────────────
        bool branchExists;
        try
        {
            var heads = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{planBranch}", ct)
                .ConfigureAwait(false);
            branchExists = heads.Count > 0;
        }
        catch (ExternalToolException ex)
        {
            EmitDetectStateError(rootId, itemId, planBranch, $"ls-remote failed: {ex.Message}");
            return ExitCodes.Success;
        }

        // ── 4. Latest PR for this branch. ─────────────────────────────────
        IReadOnlyList<PullRequestSummary> prs;
        try
        {
            prs = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: planBranch, State: "all", Limit: 50),
                ct).ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            EmitDetectStateError(rootId, itemId, planBranch,
                $"gh pr list timed out after {ex.Attempts} attempt(s)");
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitDetectStateError(rootId, itemId, planBranch, $"gh pr list failed: {ex.Message}");
            return ExitCodes.Success;
        }

        var latestPr = prs.OrderByDescending(p => p.Number).FirstOrDefault();

        // ── 5. No PR → not_started. ───────────────────────────────────────
        if (latestPr is null)
        {
            EmitDetectState(new PlanDetectStateResult
            {
                RootId = rootId,
                ItemId = itemId,
                PlanBranch = planBranch,
                State = "not_started",
                BranchExistsOnOrigin = branchExists,
            });
            return ExitCodes.Success;
        }

        // ── 6. Fetch full PR data (state + body for front-matter). ────────
        GhPullRequestPollData? pollData;
        try
        {
            pollData = await gh.GetPullRequestPollDataAsync(slug, latestPr.Number, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            EmitDetectStateError(rootId, itemId, planBranch,
                $"gh pr view timed out after {ex.Attempts} attempt(s)");
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitDetectStateError(rootId, itemId, planBranch, $"gh pr view failed: {ex.Message}");
            return ExitCodes.Success;
        }
        if (pollData is null)
        {
            EmitDetectStateError(rootId, itemId, planBranch,
                $"PR #{latestPr.Number} disappeared between list and view");
            return ExitCodes.Success;
        }

        var prState = pollData.State.ToUpperInvariant();
        var prUrl = latestPr.Url ?? string.Empty;

        // ── 7. Branch on PR state. ────────────────────────────────────────
        if (prState == "OPEN")
        {
            // Stale-generation check: compare the PR's snapshot against the
            // current manifest. If any ancestor's manifest plan-generation
            // has advanced past the snapshot, the PR was opened against an
            // older parent plan and needs to be re-derived.
            var stale = await ComputeStaleAncestorsAsync(
                manifestRef, manifestPath, pollData.Body, ct).ConfigureAwait(false);
            if (stale.Count > 0)
            {
                EmitDetectState(new PlanDetectStateResult
                {
                    RootId = rootId,
                    ItemId = itemId,
                    PlanBranch = planBranch,
                    State = "stale_generation",
                    BranchExistsOnOrigin = branchExists,
                    PrNumber = latestPr.Number,
                    PrUrl = prUrl,
                    PrState = prState,
                    StaleAncestors = stale,
                });
                return ExitCodes.Success;
            }

            EmitDetectState(new PlanDetectStateResult
            {
                RootId = rootId,
                ItemId = itemId,
                PlanBranch = planBranch,
                State = "awaiting_review",
                BranchExistsOnOrigin = branchExists,
                PrNumber = latestPr.Number,
                PrUrl = prUrl,
                PrState = prState,
            });
            return ExitCodes.Success;
        }

        if (prState == "CLOSED")
        {
            EmitDetectState(new PlanDetectStateResult
            {
                RootId = rootId,
                ItemId = itemId,
                PlanBranch = planBranch,
                State = "closed_unmerged",
                BranchExistsOnOrigin = branchExists,
                PrNumber = latestPr.Number,
                PrUrl = prUrl,
                PrState = prState,
            });
            return ExitCodes.Success;
        }

        // MERGED — discriminate complete vs merged_unseeded via the planned tag.
        var seeded = await IsParentSeededAsync(itemId, plannedTag, ct).ConfigureAwait(false);
        if (!seeded)
        {
            EmitDetectState(new PlanDetectStateResult
            {
                RootId = rootId,
                ItemId = itemId,
                PlanBranch = planBranch,
                State = "merged_unseeded",
                BranchExistsOnOrigin = branchExists,
                PrNumber = latestPr.Number,
                PrUrl = prUrl,
                PrState = prState,
            });
            return ExitCodes.Success;
        }

        // Item's plan is merged AND children are seeded. Before declaring
        // "complete", check whether any direct child has an approved plan
        // PR with `requests_parent_change: true` against this item's
        // current plan generation. If so, override to parent_change_pending
        // so plan-level.yaml routes to the parent replan loop.
        var pendingChildren = await CheckChildrenForParentChangeRequestsAsync(
            rootId, itemId, slug, manifestRef, manifestPath, ct)
            .ConfigureAwait(false);

        EmitDetectState(new PlanDetectStateResult
        {
            RootId = rootId,
            ItemId = itemId,
            PlanBranch = planBranch,
            State = pendingChildren.Count > 0 ? "parent_change_pending" : "complete",
            BranchExistsOnOrigin = branchExists,
            PrNumber = latestPr.Number,
            PrUrl = prUrl,
            PrState = prState,
            ParentChangePendingChildren = pendingChildren,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Enumerate direct children of <paramref name="parentItemId"/>, find any
    /// open plan PRs whose review state is APPROVED, body has
    /// <c>requests_parent_change: true</c>, and
    /// <c>ancestor_plan_generations[parent_key]</c> equals the parent's
    /// current plan generation in the manifest. Returns an entry per
    /// qualifying child PR. Empty list when nothing qualifies (the common
    /// case — the helper short-circuits cheap signals first).
    /// </summary>
    /// <remarks>
    /// Best-effort: any failure to fetch children, manifest, or PR data
    /// degrades to "no signal" (empty list) rather than failing the verb,
    /// matching the existing detect-state fault-tolerance posture.
    /// </remarks>
    private async Task<IReadOnlyList<ChildPendingParentChange>> CheckChildrenForParentChangeRequestsAsync(
        int rootId,
        int parentItemId,
        string slug,
        string manifestRef,
        string manifestPath,
        CancellationToken ct)
    {
        // Snapshot key convention: "root" when parent IS root; "<parent_id>" otherwise.
        var snapshotKey = parentItemId == rootId
            ? "root"
            : parentItemId.ToString(CultureInfo.InvariantCulture);

        // Read parent's current generation from the manifest. If we cannot
        // load it, no signal — return empty.
        int parentCurrentGen;
        try
        {
            var manifestYaml = await git.ShowFileAtRefAsync(manifestRef, manifestPath, ct)
                .ConfigureAwait(false);
            if (manifestYaml is null) return [];

            var manifest = RunManifestStore.Parse(manifestYaml, manifestPath);
            RunManifestValidator.ValidateOrThrow(manifest);
            if (!manifest.PlanGenerations.TryGetValue(snapshotKey, out parentCurrentGen))
            {
                // Parent is "complete" but manifest has no generation entry —
                // shouldn't happen if pr merge-plan-pr is consistent, but
                // skip rather than crash.
                return [];
            }
        }
        catch
        {
            return [];
        }

        // Enumerate direct children via twig.
        JsonNode? tree;
        try
        {
            tree = await twig.ShowTreeAsync(parentItemId, ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        if (tree?["children"] is not JsonArray children || children.Count == 0)
        {
            return [];
        }

        if (!RootId.TryParse(rootId, out var rootIdTyped))
        {
            return [];
        }

        var results = new List<ChildPendingParentChange>();
        foreach (var childNode in children.OfType<JsonObject>())
        {
            var childId = childNode["id"]?.GetValue<int?>() ?? 0;
            if (childId <= 0) continue;
            if (!WorkItemId.TryParse(childId, out var childIdTyped)) continue;

            var childPlanBranch = BranchNameBuilder.DescendantPlan(rootIdTyped, childIdTyped).Value;

            IReadOnlyList<PullRequestSummary> childPrs;
            try
            {
                childPrs = await gh.ListPullRequestsAsync(
                    slug,
                    new PrListFilters(Head: childPlanBranch, State: "open", Limit: 10),
                    ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var pr in childPrs.OrderByDescending(p => p.Number))
            {
                GhPullRequestPollData? pollData;
                try
                {
                    pollData = await gh.GetPullRequestPollDataAsync(slug, pr.Number, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                if (pollData is null) continue;

                // Approved? Match the public PrCommands.PollStatus mapping.
                var isApproved = string.Equals(pollData.State, "OPEN", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pollData.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase);
                if (!isApproved) continue;

                var fm = PlanPrFrontMatter.Parse(pollData.Body ?? string.Empty);
                if (!fm.RequestsParentChange) continue;
                if (!fm.AncestorPlanGenerations.TryGetValue(snapshotKey, out var snapshotGen)) continue;
                if (snapshotGen != parentCurrentGen) continue;

                results.Add(new ChildPendingParentChange(
                    ChildItemId: childId,
                    PrNumber: pr.Number,
                    PrUrl: pr.Url ?? string.Empty,
                    ExpectedParentGeneration: snapshotGen));

                // One qualifying PR per child is enough; later open PRs for
                // the same branch are stale by definition (we already took
                // the highest-numbered one via OrderByDescending).
                break;
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<string>> ComputeStaleAncestorsAsync(
        string manifestRef,
        string manifestPath,
        string prBody,
        CancellationToken ct)
    {
        // Front-matter parsing is best-effort — if we can't parse it, we
        // don't claim stale (no signal).
        var metadata = PlanPrFrontMatter.Parse(prBody);
        if (metadata.AncestorPlanGenerations.Count == 0) return [];

        // Manifest read mirrors PrCommands.OpenPlanPr: pull from the feature
        // branch blob, not the local working tree (the workflow runs on the
        // plan branch where the manifest doesn't exist).
        string? manifestYaml;
        try
        {
            manifestYaml = await git.ShowFileAtRefAsync(manifestRef, manifestPath, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolException)
        {
            return [];
        }
        if (manifestYaml is null) return [];

        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.Parse(manifestYaml, manifestPath);
            RunManifestValidator.ValidateOrThrow(manifest);
        }
        catch
        {
            return [];
        }

        var stale = new List<string>();
        foreach (var (ancestorKey, snapshotGen) in metadata.AncestorPlanGenerations)
        {
            if (manifest.PlanGenerations.TryGetValue(ancestorKey, out var currentGen)
                && currentGen > snapshotGen)
            {
                stale.Add($"{ancestorKey}: snapshot={snapshotGen}, manifest={currentGen}");
            }
        }
        return stale;
    }

    private async Task<bool> IsParentSeededAsync(int itemId, string plannedTag, CancellationToken ct)
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

    private async Task<string> TryResolveSlugAsync(CancellationToken ct)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return string.Empty;
            var match = GitHubSlugRegexDetect.Match(url);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void EmitDetectState(PlanDetectStateResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanDetectStateResult));

    private static void EmitDetectStateError(int rootId, int itemId, string planBranch, string error)
        => EmitDetectState(new PlanDetectStateResult
        {
            RootId = rootId,
            ItemId = itemId,
            PlanBranch = planBranch,
            State = "error",
            BranchExistsOnOrigin = false,
            Error = error,
        });
}
