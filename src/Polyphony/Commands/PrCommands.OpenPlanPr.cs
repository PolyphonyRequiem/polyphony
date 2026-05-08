using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a plan branch
    /// into its parent plan branch (or the feature branch for the root
    /// plan). Embeds two well-known YAML front-matter keys at the top
    /// of the PR body:
    /// <list type="bullet">
    ///   <item><c>requests_parent_change</c> — defaults to <c>false</c>; the planning workflow flips it to <c>true</c> when the child plan needs an amendment to its parent's plan.</item>
    ///   <item><c>ancestor_plan_generations</c> — snapshot of each ancestor plan's <c>plan_generation</c> at branch creation, used by the Phase 3 P6 stale-generation block.</item>
    /// </list>
    /// Reuse semantics: if an open PR exists for the same head branch,
    /// the verb fetches its body, parses the embedded snapshot, and:
    /// <list type="bullet">
    ///   <item>If the snapshot matches the current manifest, returns the existing PR (<c>created=false, stale=false</c>) — the verb is idempotent.</item>
    ///   <item>If the snapshot is stale (any ancestor's manifest generation has advanced past the embedded value), returns <c>created=false, stale=true</c> with a non-zero exit code so the operator can decide. The verb refuses to silently rewrite the PR body — that's a P9 concern (ancestor cascade).</item>
    /// </list>
    /// Fails with <c>RoutingFailure</c> when the head/base branch is missing on the remote, or with <c>CacheError</c> when the manifest cannot be read from <c>origin/feature/{root}</c>.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the item this plan PR belongs to. Equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="parentItemId">Immediate plan-tree parent's work-item id. Required for descendants of descendants; omit for root plan and direct children of root plan.</param>
    /// <param name="ancestorIds">Comma-separated ancestor chain (immediate parent first), used to compute the snapshot. For a child of root: <c>"root"</c>. For a deeper descendant: e.g. <c>"5678,root"</c>. Empty for the root plan.</param>
    /// <param name="manifestPath">Path to the run manifest within the <c>origin/feature/{root}</c> blob. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the cached work-item title.</param>
    /// <param name="body">Optional PR body summary (rendered after the front-matter); minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-plan-pr")]
    [VerbResult(typeof(PrOpenPlanPrResult))]
    public async Task<int> OpenPlanPr(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int parentItemId = 0,
        string ancestorIds = "",
        string manifestPath = RunManifestStore.DefaultRelativePath,
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-plan-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate input + derive head/base + ancestor chain. ────────
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitPlanPrError(rootId, itemId, parentItemId, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitPlanPrError(rootId, itemId, parentItemId, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        bool isRootPlan = itemId == rootId;
        string itemKey;
        string headBranch;
        string baseBranch;
        int? resolvedParent = null;

        if (isRootPlan)
        {
            if (parentItemId != 0)
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    $"--parent-item-id must not be provided when --item-id == --root-id (got {parentItemId}); the root plan has no parent.");
                return ExitCodes.ConfigError;
            }
            itemKey = "root";
            headBranch = BranchNameBuilder.RootPlan(root).Value;
            baseBranch = BranchNameBuilder.Feature(root).Value;
        }
        else
        {
            if (parentItemId == 0)
            {
                resolvedParent = null;
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.RootPlan(root).Value;
            }
            else
            {
                if (!WorkItemId.TryParse(parentItemId, out var parentItem))
                {
                    EmitPlanPrError(rootId, itemId, parentItemId, $"--parent-item-id must be positive (got {parentItemId})");
                    return ExitCodes.ConfigError;
                }
                if (parentItemId == itemId)
                {
                    EmitPlanPrError(rootId, itemId, parentItemId,
                        $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");
                    return ExitCodes.ConfigError;
                }
                if (parentItemId == rootId)
                {
                    EmitPlanPrError(rootId, itemId, parentItemId,
                        $"--parent-item-id ({parentItemId}) equals --root-id; omit --parent-item-id when the parent is the root plan.");
                    return ExitCodes.ConfigError;
                }
                resolvedParent = parentItemId;
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
            }
            itemKey = itemId.ToString(CultureInfo.InvariantCulture);
        }

        if (!TryParseAncestorChain(ancestorIds, isRootPlan, itemKey, out var ancestorKeys, out var ancestorError))
        {
            EmitPlanPrError(rootId, itemId, parentItemId, ancestorError, headBranch, baseBranch);
            return ExitCodes.ConfigError;
        }

        // ── 2. Read manifest from origin/feature/{root} + compute snapshot. ─
        // The manifest is owned by the feature branch, NOT by the plan branch
        // we are PR-promoting from. Reading the local working tree would pick
        // up whatever the workflow happens to have checked out (often the
        // plan branch itself). Always read from the remote feature ref so the
        // snapshot reflects the authoritative manifest at PR-open time.
        IReadOnlyDictionary<string, int> snapshot;
        var featureBranch = BranchNameBuilder.Feature(root).Value;
        var manifestRef = $"origin/{featureBranch}";
        try
        {
            var manifestYaml = await git.ShowFileAtRefAsync(manifestRef, manifestPath, ct).ConfigureAwait(false);
            if (manifestYaml is null)
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    $"manifest not found at {manifestRef}:{manifestPath} — ensure the feature branch has the manifest committed and pushed",
                    headBranch, baseBranch);
                return ExitCodes.CacheError;
            }
            var manifest = RunManifestStore.Parse(manifestYaml, $"{manifestRef}:{manifestPath}");
            RunManifestValidator.ValidateOrThrow(manifest, $"{manifestRef}:{manifestPath}");
            snapshot = ComputeSnapshot(manifest.PlanGenerations, ancestorKeys);
        }
        catch (InvalidOperationException ex)
        {
            EmitPlanPrError(rootId, itemId, parentItemId, $"manifest invalid: {ex.Message}", headBranch, baseBranch);
            return ExitCodes.CacheError;
        }
        catch (ExternalToolException ex)
        {
            EmitPlanPrError(rootId, itemId, parentItemId,
                $"git show failed for {manifestRef}:{manifestPath}: {ex.Message}",
                headBranch, baseBranch);
            return ExitCodes.CacheError;
        }

        // ── 3. Confirm head + base exist on the remote. ────────────────────
        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    $"head branch '{headBranch}' does not exist on remote — run 'polyphony branch ensure-plan' and push first",
                    headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }
            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    $"base branch '{baseBranch}' does not exist on remote — ensure the parent plan branch is materialized first",
                    headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    "Could not resolve repo slug from origin remote",
                    headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var prTitle = string.IsNullOrWhiteSpace(title)
                ? await ResolvePlanPrTitleAsync(itemId, isRootPlan, ct).ConfigureAwait(false)
                : title;
            var summaryBody = string.IsNullOrWhiteSpace(body)
                ? BuildDefaultPlanBodySummary(rootId, itemId, isRootPlan, headBranch, baseBranch)
                : body;
            var fullBody = BuildPlanPrBody(snapshot, summaryBody);

            // ── 4. Reuse check: existing open PR? ─────────────────────────
            var existing = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                ct).ConfigureAwait(false);

            if (existing.Count > 0)
            {
                var found = existing[0];
                // Re-read body to inspect embedded snapshot. ListPullRequests
                // doesn't return body; use the poll-status fetcher (P4).
                var pollData = await gh.GetPullRequestPollDataAsync(slug, found.Number, ct).ConfigureAwait(false);
                var existingMeta = pollData is null
                    ? new PrPollMetadata
                    {
                        RequestsParentChange = false,
                        AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
                    }
                    : PlanPrFrontMatter.Parse(pollData.Body);

                if (SnapshotsEquivalent(existingMeta.AncestorPlanGenerations, snapshot))
                {
                    EmitPlanPr(new PrOpenPlanPrResult
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        ParentItemId = resolvedParent ?? 0,
                        ItemKey = itemKey,
                        IsRootPlan = isRootPlan,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        PrNumber = found.Number,
                        PrUrl = found.Url ?? string.Empty,
                        Title = prTitle,
                        Created = false,
                        Stale = false,
                        RequestsParentChange = existingMeta.RequestsParentChange,
                        AncestorPlanGenerations = existingMeta.AncestorPlanGenerations,
                    });
                    return ExitCodes.Success;
                }

                EmitPlanPr(new PrOpenPlanPrResult
                {
                    RootId = rootId,
                    ItemId = itemId,
                    ParentItemId = resolvedParent ?? 0,
                    ItemKey = itemKey,
                    IsRootPlan = isRootPlan,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    RepoSlug = slug,
                    PrNumber = found.Number,
                    PrUrl = found.Url ?? string.Empty,
                    Title = prTitle,
                    Created = false,
                    Stale = true,
                    RequestsParentChange = existingMeta.RequestsParentChange,
                    AncestorPlanGenerations = existingMeta.AncestorPlanGenerations,
                    Error = BuildStaleMessage(existingMeta.AncestorPlanGenerations, snapshot),
                });
                return ExitCodes.RoutingFailure;
            }

            // ── 5. Create the PR. ─────────────────────────────────────────
            var url = await gh.CreatePullRequestAsync(slug, baseBranch, headBranch, prTitle, fullBody, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                EmitPlanPrError(rootId, itemId, parentItemId,
                    "gh pr create failed — no URL returned",
                    headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var trimmedUrl = url.Trim();
            EmitPlanPr(new PrOpenPlanPrResult
            {
                RootId = rootId,
                ItemId = itemId,
                ParentItemId = resolvedParent ?? 0,
                ItemKey = itemKey,
                IsRootPlan = isRootPlan,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                RepoSlug = slug,
                PrNumber = ExtractPrNumber(trimmedUrl),
                PrUrl = trimmedUrl,
                Title = prTitle,
                Created = true,
                Stale = false,
                RequestsParentChange = false,
                AncestorPlanGenerations = snapshot,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitPlanPrError(rootId, itemId, parentItemId, ex.Message, headBranch, baseBranch);
            return ExitCodes.RoutingFailure;
        }
    }

    /// <summary>
    /// Parse the comma-separated ancestor chain. Mirrors the validation
    /// in <c>polyphony manifest read-plan-generation-snapshot</c>:
    /// trims entries, rejects the item itself appearing in the chain,
    /// rejects duplicates, rejects empty / non-numeric entries except
    /// the literal <c>"root"</c>. For root plans the chain MUST be empty.
    /// </summary>
    private static bool TryParseAncestorChain(
        string raw,
        bool isRootPlan,
        string itemKey,
        out List<string> keys,
        out string error)
    {
        keys = new List<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!isRootPlan)
            {
                // A descendant with no ancestors at all is a programmer
                // error — the workflow always knows at least the root.
                error = "--ancestor-ids must list the ancestor plan chain (immediate parent first); empty is only valid for the root plan.";
                return false;
            }
            return true;
        }

        if (isRootPlan)
        {
            error = "root plan must not declare ancestors (got --ancestor-ids with entries).";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized;
            if (string.Equals(part, "root", StringComparison.Ordinal))
            {
                normalized = "root";
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt) && asInt > 0)
            {
                normalized = asInt.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                error = $"--ancestor-ids entry '{part}' must be 'root' or a positive numeric work-item id.";
                return false;
            }

            if (string.Equals(normalized, itemKey, StringComparison.Ordinal))
            {
                error = $"--ancestor-ids contains the item itself ('{itemKey}'); the item must not appear in its own ancestor chain.";
                return false;
            }

            if (!seen.Add(normalized))
            {
                error = $"--ancestor-ids contains duplicate entry '{normalized}'.";
                return false;
            }

            keys.Add(normalized);
        }

        return true;
    }

    /// <summary>Project the manifest's plan_generations onto the supplied ancestor chain. Missing entries default to 0 (matches the manifest snapshot semantics in P1).</summary>
    private static IReadOnlyDictionary<string, int> ComputeSnapshot(
        IReadOnlyDictionary<string, int> manifestGenerations,
        IReadOnlyList<string> ancestorKeys)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in ancestorKeys)
        {
            snapshot[key] = manifestGenerations.TryGetValue(key, out var g) ? g : 0;
        }
        return snapshot;
    }

    private static bool SnapshotsEquivalent(
        IReadOnlyDictionary<string, int> a,
        IReadOnlyDictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var other)) return false;
            if (other != kvp.Value) return false;
        }
        return true;
    }

    private static string BuildStaleMessage(
        IReadOnlyDictionary<string, int> existing,
        IReadOnlyDictionary<string, int> current)
    {
        var sb = new StringBuilder();
        sb.Append("existing plan PR's embedded ancestor_plan_generations snapshot is stale; ");
        sb.Append("close + reopen with current snapshot, or rebase + amend the front-matter. ");
        sb.Append("existing={");
        sb.Append(string.Join(",", existing.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}")));
        sb.Append("} current={");
        sb.Append(string.Join(",", current.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}")));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Render the PR body with the well-known YAML front-matter at the
    /// top, followed by a blank line, then the human-readable summary.
    /// Format pinned to match what <see cref="PlanPrFrontMatter"/> can
    /// parse back out (and what the Phase 3 ADR specified). Both keys
    /// are always emitted to keep the front-matter shape stable.
    /// </summary>
    private static string BuildPlanPrBody(
        IReadOnlyDictionary<string, int> snapshot,
        string summary)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("requests_parent_change: false\n");
        if (snapshot.Count == 0)
        {
            sb.Append("ancestor_plan_generations: {}\n");
        }
        else
        {
            sb.Append("ancestor_plan_generations:\n");
            foreach (var key in snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                // Quote the key when it's purely numeric so YAML doesn't
                // promote it to an integer and lose round-trip fidelity.
                var displayKey = string.Equals(key, "root", StringComparison.Ordinal) ? key : $"\"{key}\"";
                sb.Append("  ").Append(displayKey).Append(": ").Append(snapshot[key].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        sb.Append("---\n\n");
        sb.Append(summary);
        if (!summary.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    private async Task<string> ResolvePlanPrTitleAsync(int itemId, bool isRootPlan, CancellationToken ct)
    {
        var fallback = isRootPlan
            ? $"plan: root #{itemId}"
            : $"plan: #{itemId}";
        try
        {
            var tree = await twig.ShowTreeAsync(itemId, ct).ConfigureAwait(false);
            var workItemTitle = tree?["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(workItemTitle)) return fallback;
            return $"plan: {workItemTitle} AB#{itemId}";
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildDefaultPlanBodySummary(
        int rootId,
        int itemId,
        bool isRootPlan,
        string headBranch,
        string baseBranch)
    {
        var sb = new StringBuilder();
        sb.Append(isRootPlan
            ? $"## Root plan for #{rootId}\n\n"
            : $"## Plan for #{itemId} (root #{rootId})\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("AB#").Append(itemId).Append('\n');
        return sb.ToString();
    }

    private static void EmitPlanPr(PrOpenPlanPrResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenPlanPrResult));

    private static void EmitPlanPrError(
        int rootId,
        int itemId,
        int parentItemId,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        var itemKey = itemId == rootId
            ? "root"
            : itemId.ToString(CultureInfo.InvariantCulture);
        EmitPlanPr(new PrOpenPlanPrResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = itemId == rootId,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RepoSlug = string.Empty,
            PrNumber = 0,
            PrUrl = string.Empty,
            Title = string.Empty,
            Created = false,
            Stale = false,
            RequestsParentChange = false,
            AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
            Error = message,
        });
    }
}
