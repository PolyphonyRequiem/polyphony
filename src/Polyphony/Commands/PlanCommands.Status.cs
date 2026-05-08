using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Twig.Domain.Aggregates;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan status</c> — operator-facing tree-walked snapshot of
/// the plan-PR state of every plannable item under a run root.
///
/// <para>Walks the in-scope subtree from <c>--root</c> (the same BFS the
/// worklist build verb uses), classifies each item's plannable facet from
/// the loaded <see cref="ProcessConfig"/>, and queries <c>gh</c> per
/// plan branch (<c>plan/{root}</c> or <c>plan/{root}-{item}</c>) to
/// derive the <see cref="PlanStatusItem.PlanStatus"/> enum value:
/// <c>needed</c> | <c>open</c> | <c>merged</c> | <c>abandoned</c> |
/// <c>n/a</c>. Plan generation is enriched from the run manifest's
/// <see cref="RunManifest.PlanGenerations"/> map when the manifest is
/// available; manifest absence is non-fatal — the verb still walks and
/// reports on the tree, leaving generation null.</para>
///
/// <para>Pure inspection verb: read-only against twig and the manifest;
/// no mutation, no platform writes. Routing-style envelope: ALWAYS
/// exits 0; consumers branch on <see cref="PlanStatusResult.ErrorCode"/>.</para>
///
/// <para><b>Phase 3 P10.</b> Supersedes the P9 manifest-only ledger
/// reader — that earlier shape only knew about merged plan PRs and
/// could not surface "needed" or "open with revisions requested" rows,
/// which the operator-facing dashboard requires.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Snapshot the plan-PR state of every plannable item under a run root.
    /// </summary>
    /// <param name="root">Run-root work-item id (positive).</param>
    /// <param name="manifest">Path to the run manifest. When omitted,
    /// defaults to <see cref="RunManifestStore.DefaultRelativePath"/>
    /// resolved relative to the current working directory; the manifest
    /// is optional — if it is absent <see cref="PlanStatusItem.PlanGeneration"/>
    /// is left null and the rest of the snapshot still renders.</param>
    /// <param name="repo">Optional <c>owner/repo</c> slug. When omitted the
    /// slug is derived from <c>git remote get-url origin</c>; an unresolvable
    /// slug surfaces as <c>no_repo_slug</c>.</param>
    /// <param name="includeNa">When true, items with no plannable facet are
    /// included in the items array (still tagged <c>plan_status="n/a"</c>).
    /// Default false — operators usually want only the actionable rows; the
    /// summary counters always include n/a items so total scope is visible.</param>
    /// <param name="json">Emit the machine-readable JSON envelope instead of the
    /// human-readable table.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    [VerbResult(typeof(PlanStatusResult))]
    public async Task<int> Status(
        int root,
        string manifest = "",
        string repo = "",
        bool includeNa = false,
        bool json = false,
        CancellationToken ct = default)
    {
        if (root <= 0)
        {
            EmitStatus(ErrorResult(root, "invalid_argument", $"--root must be positive (got {root})"), json, includeNa);
            return ExitCodes.Success;
        }

        // ── 1. Walk the subtree. ────────────────────────────────────────
        List<WalkedPlanItem> walked;
        try
        {
            walked = await WalkPlanSubtreeAsync(root, ct).ConfigureAwait(false);
        }
        catch (PlanStatusFailure ex)
        {
            EmitStatus(ErrorResult(root, ex.Code, ex.Message), json, includeNa);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitStatus(ErrorResult(root, "cache_error", ex.Message), json, includeNa);
            return ExitCodes.Success;
        }

        // ── 2. Optional manifest enrichment for plan_generation. ───────
        // Manifest is OPTIONAL on this verb — absence leaves PlanGeneration
        // null. Explicit --manifest path that points at a missing/invalid
        // file is still an error, because the operator was specific.
        var manifestExplicit = !string.IsNullOrEmpty(manifest);
        var manifestPath = manifestExplicit ? manifest : RunManifestStore.DefaultRelativePath;
        RunManifest? loadedManifest = null;
        if (manifestExplicit || File.Exists(manifestPath))
        {
            try
            {
                loadedManifest = RunManifestStore.LoadOrThrow(manifestPath);
            }
            catch (FileNotFoundException ex)
            {
                EmitStatus(ErrorResult(root, "manifest_not_found", ex.Message), json, includeNa);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitStatus(ErrorResult(root, "manifest_invalid", ex.Message), json, includeNa);
                return ExitCodes.Success;
            }

            if (loadedManifest.RootId != root)
            {
                EmitStatus(
                    ErrorResult(root, "root_id_mismatch",
                        $"Manifest root_id {loadedManifest.RootId} does not match --root {root}"),
                    json, includeNa);
                return ExitCodes.Success;
            }
        }

        // ── 3. Determine which items have plannable facets. ────────────
        // Items without the plannable facet stay in the walked list (so
        // summary.total_items reflects the full tree) but get tagged "n/a".
        var classified = ClassifyPlannable(walked);

        // ── 4. Resolve repo slug only if at least one item is plannable. ─
        // Otherwise we'd surface a spurious no_repo_slug error on a tree
        // composed entirely of leaf tasks.
        var anyPlannable = classified.Any(c => c.IsPlannable);
        string slug = "";
        if (anyPlannable)
        {
            slug = !string.IsNullOrWhiteSpace(repo)
                ? repo.Trim()
                : await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitStatus(
                    ErrorResult(root, "no_repo_slug",
                        "Could not resolve owner/repo slug from origin remote — pass --repo explicitly."),
                    json, includeNa);
                return ExitCodes.Success;
            }
        }

        // ── 5. Per-item PR enumeration via gh. ─────────────────────────
        var items = new List<PlanStatusItem>(classified.Count);
        foreach (var c in classified)
        {
            ct.ThrowIfCancellationRequested();

            if (!c.IsPlannable)
            {
                items.Add(new PlanStatusItem
                {
                    ItemId = c.Walked.Item.Id,
                    Title = c.Walked.Item.Title ?? "",
                    PlanStatus = "n/a",
                });
                continue;
            }

            var headBranch = c.Walked.Item.Id == root
                ? $"plan/{root}"
                : $"plan/{root}-{c.Walked.Item.Id}";

            PlanPrSnapshot snapshot;
            try
            {
                snapshot = await ResolvePlanPrAsync(slug, headBranch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitStatus(
                    ErrorResult(root, "gh_failed",
                        $"gh PR enumeration failed for branch '{headBranch}' (item {c.Walked.Item.Id}): {ex.Message}"),
                    json, includeNa);
                return ExitCodes.Success;
            }

            var generation = snapshot.Status switch
            {
                "open" or "merged" or "abandoned" => LookupGeneration(loadedManifest, root, c.Walked.Item.Id),
                _ => (int?)null,
            };

            items.Add(new PlanStatusItem
            {
                ItemId = c.Walked.Item.Id,
                Title = c.Walked.Item.Title ?? "",
                PlanStatus = snapshot.Status,
                PlanPrNumber = snapshot.PrNumber,
                PlanPrUrl = snapshot.Url,
                PlanGeneration = generation,
                PendingRevisions = snapshot.PendingRevisions,
            });
        }

        items.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
        var summary = BuildSummary(items);

        EmitStatus(new PlanStatusResult
        {
            Success = true,
            RootId = root,
            Items = items,
            Summary = summary,
        }, json, includeNa);
        return ExitCodes.Success;
    }

    // ─── BFS walk ──────────────────────────────────────────────────────

    private async Task<List<WalkedPlanItem>> WalkPlanSubtreeAsync(int rootId, CancellationToken ct)
    {
        var rootItem = await repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (rootItem is null)
        {
            throw new PlanStatusFailure("root_not_found", $"Run root {rootId} not found in twig cache.");
        }

        var walked = new List<WalkedPlanItem> { new(rootItem) };
        var seen = new HashSet<int> { rootId };
        var current = new List<int> { rootId };

        while (current.Count > 0)
        {
            var next = new List<int>();
            foreach (var parentId in current)
            {
                ct.ThrowIfCancellationRequested();
                var children = await repository.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
                foreach (var child in children.OrderBy(c => c.Id))
                {
                    if (!seen.Add(child.Id)) continue;
                    walked.Add(new WalkedPlanItem(child));
                    next.Add(child.Id);
                }
            }
            current = next;
        }

        return walked;
    }

    // ─── Facet classification ─────────────────────────────────────────

    private List<ClassifiedPlanItem> ClassifyPlannable(IReadOnlyList<WalkedPlanItem> walked)
    {
        var classified = new List<ClassifiedPlanItem>(walked.Count);
        foreach (var w in walked)
        {
            var typeName = w.Item.Type.Value ?? "";
            if (string.IsNullOrEmpty(typeName) || !processConfig.Types.TryGetValue(typeName, out var typeConfig))
            {
                throw new PlanStatusFailure(
                    "type_unknown",
                    $"Type '{typeName}' (item {w.Item.Id}) not found in process config.");
            }

            var isPlannable = Array.Exists(typeConfig.Facets,
                f => string.Equals(f, "plannable", StringComparison.OrdinalIgnoreCase));
            classified.Add(new ClassifiedPlanItem(w, isPlannable));
        }
        return classified;
    }

    // ─── PR resolution via gh ─────────────────────────────────────────

    /// <summary>
    /// Three-pass enumeration: open → merged → closed. The cheapest
    /// "any open?" check fires first because it is the most operator-
    /// relevant outcome; we only fall through to merged / closed when the
    /// branch has no open PR. When an open PR exists, follows up with a
    /// single <see cref="IGhClient.GetPullRequestPollDataAsync"/> to read
    /// the review decision so the caller can populate
    /// <see cref="PlanStatusItem.PendingRevisions"/>.
    /// </summary>
    private async Task<PlanPrSnapshot> ResolvePlanPrAsync(
        string slug, string headBranch, CancellationToken ct)
    {
        // Open?
        var open = await gh.ListPullRequestsAsync(
            slug,
            new PrListFilters(Head: headBranch, State: "open", Limit: 1),
            ct).ConfigureAwait(false);
        if (open.Count > 0)
        {
            var pr = open[0];
            bool? pending = null;
            try
            {
                var poll = await gh.GetPullRequestPollDataAsync(slug, pr.Number, ct).ConfigureAwait(false);
                if (poll is not null)
                {
                    pending = string.Equals(poll.ReviewDecision, "CHANGES_REQUESTED", StringComparison.Ordinal);
                }
            }
            catch
            {
                // Pending-revisions enrichment is best-effort — if poll fails,
                // we still surface "open" with a null pending flag rather than
                // killing the whole snapshot for a single transient gh blip.
                pending = null;
            }

            return new PlanPrSnapshot(
                Status: "open",
                PrNumber: pr.Number,
                Url: pr.Url ?? BuildPrUrl(slug, pr.Number),
                PendingRevisions: pending);
        }

        // Merged?
        var merged = await gh.ListPullRequestsAsync(
            slug,
            new PrListFilters(Head: headBranch, State: "merged", Limit: 1),
            ct).ConfigureAwait(false);
        if (merged.Count > 0)
        {
            var pr = merged[0];
            return new PlanPrSnapshot(
                Status: "merged",
                PrNumber: pr.Number,
                Url: pr.Url ?? BuildPrUrl(slug, pr.Number),
                PendingRevisions: null);
        }

        // Abandoned (closed without merge)?
        var closed = await gh.ListPullRequestsAsync(
            slug,
            new PrListFilters(Head: headBranch, State: "closed", Limit: 1),
            ct).ConfigureAwait(false);
        if (closed.Count > 0)
        {
            var pr = closed[0];
            return new PlanPrSnapshot(
                Status: "abandoned",
                PrNumber: pr.Number,
                Url: pr.Url ?? BuildPrUrl(slug, pr.Number),
                PendingRevisions: null);
        }

        return new PlanPrSnapshot("needed", null, null, null);
    }

    private static string BuildPrUrl(string slug, int prNumber)
        => $"https://github.com/{slug}/pull/{prNumber.ToString(CultureInfo.InvariantCulture)}";

    // ─── Manifest enrichment ──────────────────────────────────────────

    private static int? LookupGeneration(RunManifest? manifest, int rootId, int itemId)
    {
        if (manifest is null) return null;
        var key = itemId == rootId ? "root" : itemId.ToString(CultureInfo.InvariantCulture);
        return manifest.PlanGenerations.TryGetValue(key, out var gen) ? gen : (int?)null;
    }

    // ─── Summary ──────────────────────────────────────────────────────

    private static PlanStatusSummary BuildSummary(IReadOnlyList<PlanStatusItem> items)
    {
        var s = new
        {
            Total = items.Count,
            Needed = 0,
            Open = 0,
            Merged = 0,
            Abandoned = 0,
            Na = 0,
            Pending = 0,
        };
        var needed = 0;
        var open = 0;
        var merged = 0;
        var abandoned = 0;
        var na = 0;
        var pending = 0;
        foreach (var item in items)
        {
            switch (item.PlanStatus)
            {
                case "needed": needed++; break;
                case "open":
                    open++;
                    if (item.PendingRevisions == true) pending++;
                    break;
                case "merged": merged++; break;
                case "abandoned": abandoned++; break;
                case "n/a": na++; break;
            }
        }
        return new PlanStatusSummary
        {
            TotalItems = s.Total,
            PlanNeeded = needed,
            PlanOpen = open,
            PlanMerged = merged,
            PlanAbandoned = abandoned,
            PlanNa = na,
            PendingRevisions = pending,
        };
    }

    // ─── Emission ─────────────────────────────────────────────────────

    private static PlanStatusResult ErrorResult(int rootId, string code, string message)
        => new()
        {
            Success = false,
            RootId = rootId,
            Items = Array.Empty<PlanStatusItem>(),
            Summary = PlanStatusSummary.Empty,
            ErrorCode = code,
            ErrorMessage = message,
        };

    private static void EmitStatus(PlanStatusResult result, bool json, bool includeNa)
    {
        // Apply --include-na filter at the emit boundary so the in-flight
        // count stays available for the summary.
        var emitted = includeNa
            ? result
            : result with
            {
                Items = result.Items
                    .Where(i => !string.Equals(i.PlanStatus, "n/a", StringComparison.Ordinal))
                    .ToArray(),
            };

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                emitted, PolyphonyJsonContext.Default.PlanStatusResult));
            return;
        }

        Console.WriteLine(RenderHumanStatus(emitted));
    }

    /// <summary>
    /// Human-readable table. Layout:
    /// <code>
    /// plan status: root=100  items=4  needed=1 open=1 merged=1 abandoned=0 n/a=1  pending_revisions=0
    ///   ITEM    STATUS      PR    GENERATION  TITLE
    ///   100     merged      #142  1           Apex epic
    ///   1101    open*       #145  2           Sub-issue A          (* = changes requested)
    ///   1102    n/a         -     -           Sub-issue B (no plan facet)
    ///   1103    needed      -     -           Sub-issue C (planning needed but not started)
    /// </code>
    /// On error, body is replaced with a single error line.
    /// </summary>
    private static string RenderHumanStatus(PlanStatusResult result)
    {
        var sb = new StringBuilder();
        sb.Append("plan status: root=").Append(result.RootId.ToString(CultureInfo.InvariantCulture));

        if (result.ErrorCode is not null)
        {
            sb.AppendLine();
            sb.Append("  error: ").Append(result.ErrorMessage ?? "(no message)")
              .Append(" (").Append(result.ErrorCode).Append(')');
            return sb.ToString();
        }

        var s = result.Summary;
        sb.Append("  items=").Append(s.TotalItems.ToString(CultureInfo.InvariantCulture));
        sb.Append("  needed=").Append(s.PlanNeeded.ToString(CultureInfo.InvariantCulture));
        sb.Append(" open=").Append(s.PlanOpen.ToString(CultureInfo.InvariantCulture));
        sb.Append(" merged=").Append(s.PlanMerged.ToString(CultureInfo.InvariantCulture));
        sb.Append(" abandoned=").Append(s.PlanAbandoned.ToString(CultureInfo.InvariantCulture));
        sb.Append(" n/a=").Append(s.PlanNa.ToString(CultureInfo.InvariantCulture));
        sb.Append("  pending_revisions=").Append(s.PendingRevisions.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (result.Items.Count == 0)
        {
            sb.Append("  (no items to report — pass --include-na to see n/a rows)");
            return sb.ToString();
        }

        // Column widths: stable for the operator's eye, no fancy auto-fit.
        sb.AppendLine("  ITEM      STATUS       PR     GEN  TITLE");
        foreach (var item in result.Items)
        {
            var statusCell = item.PendingRevisions == true ? item.PlanStatus + "*" : item.PlanStatus;
            sb.Append("  ");
            sb.Append(item.ItemId.ToString(CultureInfo.InvariantCulture).PadRight(9));
            sb.Append(' ');
            sb.Append(statusCell.PadRight(12));
            sb.Append(' ');
            sb.Append((item.PlanPrNumber.HasValue
                ? "#" + item.PlanPrNumber.Value.ToString(CultureInfo.InvariantCulture)
                : "-").PadRight(6));
            sb.Append(' ');
            sb.Append((item.PlanGeneration.HasValue
                ? item.PlanGeneration.Value.ToString(CultureInfo.InvariantCulture)
                : "-").PadRight(4));
            sb.Append(' ');
            sb.Append(item.Title);
            sb.AppendLine();
        }

        if (result.Items.Any(i => i.PendingRevisions == true))
        {
            sb.Append("  (* = changes requested — open plan PR has unresolved review feedback)");
        }
        else
        {
            // Trim trailing newline produced by the last row.
            while (sb.Length > 0 && (sb[^1] == '\n' || sb[^1] == '\r'))
            {
                sb.Length--;
            }
        }

        return sb.ToString();
    }

    // ─── Internal carrier types ───────────────────────────────────────

    private sealed record WalkedPlanItem(WorkItem Item);

    private sealed record ClassifiedPlanItem(WalkedPlanItem Walked, bool IsPlannable);

    private sealed record PlanPrSnapshot(string Status, int? PrNumber, string? Url, bool? PendingRevisions);

    /// <summary>
    /// Internal control-flow exception used to short-circuit the BFS walk
    /// or facet classification when the verb cannot continue. Carries the
    /// routing-style error code the verb surfaces in its envelope.
    /// </summary>
    private sealed class PlanStatusFailure(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
