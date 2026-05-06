using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan status</c> — operator-facing read-only snapshot of the
/// plan-PR ledger across the run hierarchy. Reads
/// <c>.polyphony/run.yaml</c> and aggregates the
/// <see cref="RunManifest.MergedPlanPrs"/> entries per item, surfacing the
/// current generation, merged-PR count, and the most recent merged PR.
///
/// <para>This is a pure inspection verb: no manifest mutations, no platform
/// calls, no git mutations beyond <c>rev-parse --show-toplevel</c> for the
/// default manifest-path discovery. Always exits 0; consumers branch on the
/// JSON payload's <c>error</c> field.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Snapshot the plan-PR ledger for the given run root.
    /// </summary>
    /// <param name="rootId">Run-root work-item id (positive). MUST match
    /// the manifest's <see cref="RunManifest.RootId"/>; mismatch is an
    /// error so an operator never accidentally inspects the wrong run.</param>
    /// <param name="manifestPath">Override the manifest path. Defaults to
    /// discovering the repo root via <c>git rev-parse --show-toplevel</c>
    /// and appending <see cref="RunManifestStore.DefaultRelativePath"/>.</param>
    /// <param name="json">Emit machine-readable JSON instead of the human
    /// indented summary. The JSON shape is <see cref="PlanStatusResult"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    public async Task<int> Status(
        int rootId,
        string manifestPath = "",
        bool json = false,
        CancellationToken ct = default)
    {
        if (rootId <= 0)
        {
            EmitStatus(new PlanStatusResult
            {
                RootId = rootId,
                ManifestPath = manifestPath,
                Error = $"--root-id must be positive (got {rootId})",
            }, json);
            return ExitCodes.Success;
        }

        // Resolve manifest path: explicit override wins; else discover from git toplevel.
        var resolvedPath = manifestPath;
        if (string.IsNullOrEmpty(resolvedPath))
        {
            string? topLevel;
            try
            {
                topLevel = await git.GetTopLevelAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EmitStatus(new PlanStatusResult
                {
                    RootId = rootId,
                    ManifestPath = "",
                    Error = $"Could not resolve repo root via git rev-parse --show-toplevel: {ex.Message}",
                }, json);
                return ExitCodes.Success;
            }
            if (string.IsNullOrEmpty(topLevel))
            {
                EmitStatus(new PlanStatusResult
                {
                    RootId = rootId,
                    ManifestPath = "",
                    Error = "Could not resolve repo root via git rev-parse --show-toplevel (not a git repository?)",
                }, json);
                return ExitCodes.Success;
            }
            resolvedPath = Path.Combine(topLevel, RunManifestStore.DefaultRelativePath);
        }

        // Load manifest. RunManifestStore throws on missing/malformed/invalid;
        // we catch everything and route via the error field.
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(resolvedPath);
        }
        catch (Exception ex)
        {
            EmitStatus(new PlanStatusResult
            {
                RootId = rootId,
                ManifestPath = resolvedPath,
                Error = ex.Message,
            }, json);
            return ExitCodes.Success;
        }

        if (manifest.RootId != rootId)
        {
            EmitStatus(new PlanStatusResult
            {
                RootId = rootId,
                ManifestPath = resolvedPath,
                Error = $"Manifest root_id {manifest.RootId} does not match --root-id {rootId}",
            }, json);
            return ExitCodes.Success;
        }

        var items = AggregateLedger(manifest, rootId);

        EmitStatus(new PlanStatusResult
        {
            RootId = rootId,
            ManifestPath = resolvedPath,
            Items = items,
        }, json);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Groups <see cref="RunManifest.MergedPlanPrs"/> by item key, resolves
    /// each key to a numeric work-item id (root key → <paramref name="rootId"/>;
    /// numeric keys → parsed int), and produces the per-item summary rows
    /// sorted by item id ascending.
    /// </summary>
    private static IReadOnlyList<PlanStatusItem> AggregateLedger(RunManifest manifest, int rootId)
    {
        if (manifest.MergedPlanPrs.Count == 0) return Array.Empty<PlanStatusItem>();

        var grouped = new Dictionary<string, List<MergedPlanPrEntry>>(StringComparer.Ordinal);
        foreach (var entry in manifest.MergedPlanPrs)
        {
            if (!grouped.TryGetValue(entry.ItemKey, out var bucket))
            {
                bucket = new List<MergedPlanPrEntry>();
                grouped[entry.ItemKey] = bucket;
            }
            bucket.Add(entry);
        }

        var items = new List<PlanStatusItem>(grouped.Count);
        foreach (var (itemKey, entries) in grouped)
        {
            // "root" → root id; otherwise parse numeric. Skip silently on a
            // malformed key — the ledger validator already rejects garbage,
            // but a defensive skip keeps the verb non-fatal under future
            // schema additions.
            int itemId;
            if (string.Equals(itemKey, "root", StringComparison.Ordinal))
            {
                itemId = rootId;
            }
            else if (!int.TryParse(itemKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId))
            {
                continue;
            }

            // Most recent by RecordedAt (then PrNumber as a tiebreaker).
            var latest = entries
                .OrderByDescending(e => e.RecordedAt)
                .ThenByDescending(e => e.PrNumber)
                .First();

            // Current generation: prefer the manifest's PlanGenerations map
            // (the source of truth), falling back to the latest ledger entry's
            // CurrentGeneration so the output remains useful even if the map
            // and ledger have diverged for some reason.
            var currentGeneration = manifest.PlanGenerations.TryGetValue(itemKey, out var mapGen)
                ? mapGen
                : latest.CurrentGeneration;

            items.Add(new PlanStatusItem
            {
                ItemId = itemId,
                CurrentGeneration = currentGeneration,
                MergedPrCount = entries.Count,
                LatestPrUrl = BuildPrUrl(manifest.PlatformProject, latest.PrNumber),
                LatestMergedAt = latest.RecordedAt,
            });
        }

        items.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
        return items;
    }

    /// <summary>
    /// Builds a best-effort PR URL from the manifest's
    /// <see cref="RunManifest.PlatformProject"/> and a PR number. Currently
    /// supports the GitHub form (<c>github.com/owner/repo</c>) and falls
    /// through to <c>null</c> for other platforms (e.g. Azure DevOps),
    /// where the platform_project shape doesn't carry enough information
    /// to build a deep link.
    /// </summary>
    private static string? BuildPrUrl(string platformProject, int prNumber)
    {
        if (string.IsNullOrWhiteSpace(platformProject) || prNumber <= 0) return null;
        if (platformProject.StartsWith("github.com/", StringComparison.Ordinal))
        {
            return $"https://{platformProject}/pull/{prNumber.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    private static void EmitStatus(PlanStatusResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                result, PolyphonyJsonContext.Default.PlanStatusResult));
            return;
        }

        Console.WriteLine(RenderHuman(result));
    }

    /// <summary>
    /// Renders the human-readable form. Layout:
    /// <code>
    /// plan status: root=100  manifest=/abs/path
    ///   item 100  generation=2  merged_prs=2  latest=#42 https://...  at=2026-01-02T03:04:05Z
    ///   item 250  generation=1  merged_prs=1  latest=#43 https://...  at=2026-01-02T03:05:00Z
    /// </code>
    /// Errors render as a single line prefixed with <c>error:</c>.
    /// </summary>
    private static string RenderHuman(PlanStatusResult result)
    {
        var sb = new StringBuilder();
        sb.Append("plan status: root=").Append(result.RootId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(result.ManifestPath))
        {
            sb.Append("  manifest=").Append(result.ManifestPath);
        }
        sb.AppendLine();

        if (result.Error is not null)
        {
            sb.Append("  error: ").AppendLine(result.Error);
            return sb.ToString().TrimEnd();
        }

        if (result.Items.Count == 0)
        {
            sb.AppendLine("  (no merged plan PRs yet)");
            return sb.ToString().TrimEnd();
        }

        foreach (var item in result.Items)
        {
            sb.Append("  item ").Append(item.ItemId.ToString(CultureInfo.InvariantCulture));
            sb.Append("  generation=").Append(item.CurrentGeneration.ToString(CultureInfo.InvariantCulture));
            sb.Append("  merged_prs=").Append(item.MergedPrCount.ToString(CultureInfo.InvariantCulture));
            if (item.LatestPrUrl is not null)
            {
                sb.Append("  latest=").Append(item.LatestPrUrl);
            }
            if (item.LatestMergedAt is { } merged)
            {
                sb.Append("  at=").Append(merged.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
