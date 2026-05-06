using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan seed-children</c> — idempotent reconciliation of an
/// architect-emitted child list against the existing children of a parent
/// work item. Replaces <c>.conductor/registry/scripts/seeder.ps1</c>.
///
/// Match precedence per child entry:
/// <list type="number">
///   <item>Marker match: existing child whose description contains
///         <c>&lt;!-- polyphony:plan-child-id={id} --&gt;</c> matching the
///         architect's id → reused (no create).</item>
///   <item>Title+type match: existing child with the same (title, type) under
///         the parent → reused with a warning (marker damaged or missing).</item>
///   <item>No match → created via twig new with the marker embedded as the
///         last line of the description.</item>
/// </list>
///
/// On zero errors, merges the planned tag (default <c>polyphony:planned</c>)
/// into the parent's <c>System.Tags</c>. PhaseDetector reads this tag to
/// recognize the planned state without consulting process-config.
/// </summary>
public sealed partial class PlanCommands
{
    private static readonly Regex MarkerRegex =
        new(@"<!--\s*polyphony:plan-child-id=(task-\d+)\s*-->", RegexOptions.Compiled);

    /// <summary>
    /// Idempotently seed the architect's decomposition entries as children of
    /// <paramref name="workItem"/>.
    /// </summary>
    /// <param name="workItem">Parent work item ID.</param>
    /// <param name="childrenJson">JSON array of child objects from <c>architect.output.children</c>.
    /// Each child entry requires <c>child_id</c>, <c>title</c>, <c>type</c>, <c>description</c>.</param>
    /// <param name="plannedTag">Tag value to apply to the parent on success
    /// (defaults to <c>polyphony:planned</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("seed-children")]
    public async Task<int> SeedChildren(
        int workItem,
        string childrenJson,
        string plannedTag = "polyphony:planned",
        CancellationToken ct = default)
    {
        // Parse children (tolerate empty / null payloads — atomic items have no children to seed).
        JsonNode? childrenNode;
        try
        {
            childrenNode = string.IsNullOrWhiteSpace(childrenJson) ? null : JsonNode.Parse(childrenJson);
        }
        catch (JsonException ex)
        {
            EmitError($"children_json is not valid JSON: {ex.Message}");
            return ExitCodes.ConfigError;
        }

        var children = childrenNode is JsonArray arr ? arr : new JsonArray();

        // Snapshot existing children once — we'll match against this.
        JsonNode? tree;
        try
        {
            tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitError($"failed to read existing children of #{workItem}: {ex.Message}");
            return ExitCodes.RoutingFailure;
        }

        var (markerIndex, titleTypeIndex) = BuildIndexes(tree);

        var seeded = new List<SeedReconciliation>();
        var reused = new List<SeedReconciliation>();
        var errors = new List<SeedError>();
        var warnings = new List<string>();

        foreach (var childNode in children)
        {
            if (childNode is not JsonObject child)
            {
                errors.Add(new SeedError { Error = "child entry was not a JSON object" });
                continue;
            }

            var childId = child["child_id"]?.GetValue<string>();
            var title = child["title"]?.GetValue<string>();
            var type = child["type"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(childId))
            {
                errors.Add(new SeedError { Title = title, Error = "child entry missing required child_id" });
                continue;
            }
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(type))
            {
                errors.Add(new SeedError { ChildId = childId, Title = title, Error = "child entry missing required title or type" });
                continue;
            }

            try
            {
                if (markerIndex.TryGetValue(childId, out var hit))
                {
                    reused.Add(new SeedReconciliation { ChildId = childId, WorkItemId = hit.Id, MatchedBy = "marker" });
                    continue;
                }

                var key = $"{type}|{title}";
                if (titleTypeIndex.TryGetValue(key, out var fallbackHit))
                {
                    warnings.Add($"child {childId} matched #{fallbackHit.Id} by title fallback (marker damaged or missing)");
                    reused.Add(new SeedReconciliation { ChildId = childId, WorkItemId = fallbackHit.Id, MatchedBy = "title" });
                    continue;
                }

                var description = BuildDescription(child, childId);
                var created = await twig.CreateChildAsync(workItem, type, title, description, ct).ConfigureAwait(false);
                var newId = created["id"]?.GetValue<int>() ?? 0;
                if (newId == 0)
                {
                    errors.Add(new SeedError { ChildId = childId, Title = title, Error = "twig new returned no id" });
                    continue;
                }
                seeded.Add(new SeedReconciliation { ChildId = childId, WorkItemId = newId, MatchedBy = "created" });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add(new SeedError { ChildId = childId, Title = title, Error = ex.Message });
            }
        }

        var tagSet = false;
        var tagAlreadyPresent = false;
        if (errors.Count == 0)
        {
            try
            {
                var currentTags = await GetParentTagsAsync(workItem, ct).ConfigureAwait(false);
                if (currentTags.Contains(plannedTag, StringComparer.Ordinal))
                {
                    tagSet = true;
                    tagAlreadyPresent = true;
                }
                else
                {
                    var merged = string.Join("; ", currentTags.Append(plannedTag).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal));
                    await twig.PatchFieldsAsync(workItem,
                        new Dictionary<string, string> { ["System.Tags"] = merged }, ct).ConfigureAwait(false);
                    tagSet = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add(new SeedError
                {
                    Error = $"failed to set planned tag on parent #{workItem}: {ex.Message}",
                });
            }
        }

        var result = new PlanSeedChildrenResult
        {
            WorkItemId = workItem,
            ChildCount = children.Count,
            SeededCount = seeded.Count,
            ReusedCount = reused.Count,
            ErrorCount = errors.Count,
            SeededItems = seeded,
            ReusedItems = reused,
            Errors = errors,
            Warnings = warnings,
            PlannedTagSet = tagSet,
            PlannedTagAlready = tagAlreadyPresent,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanSeedChildrenResult));
        return ExitCodes.Success;
    }

    private static (Dictionary<string, ChildSnapshot> ByMarker, Dictionary<string, ChildSnapshot> ByTitleType)
        BuildIndexes(JsonNode? tree)
    {
        var markerIndex = new Dictionary<string, ChildSnapshot>(StringComparer.Ordinal);
        var titleTypeIndex = new Dictionary<string, ChildSnapshot>(StringComparer.Ordinal);
        if (tree?["children"] is not JsonArray children) return (markerIndex, titleTypeIndex);

        foreach (var child in children.OfType<JsonObject>())
        {
            var id = child["id"]?.GetValue<int>() ?? 0;
            if (id == 0) continue;

            var type = child["type"]?.GetValue<string>() ?? "";
            var title = child["title"]?.GetValue<string>() ?? "";
            var description = child["fields"]?["System.Description"]?.GetValue<string>();

            var snap = new ChildSnapshot(id, type, title);
            var marker = ExtractMarkerId(description);
            if (marker is not null) markerIndex[marker] = snap;
            var key = $"{type}|{title}";
            // First-seen wins to mirror the original PowerShell hashtable behaviour.
            titleTypeIndex.TryAdd(key, snap);
        }
        return (markerIndex, titleTypeIndex);
    }

    private static string? ExtractMarkerId(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var match = MarkerRegex.Match(description);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string BuildDescription(JsonObject child, string childId)
    {
        var body = child["description"]?.GetValue<string>()?.TrimEnd() ?? "";
        var sb = new System.Text.StringBuilder(body);

        if (child["acceptance_criteria"] is JsonArray ac && ac.Count > 0)
        {
            sb.Append("\n\n## Acceptance Criteria\n");
            foreach (var criterion in ac)
            {
                sb.Append("- ").Append(criterion?.GetValue<string>() ?? "").Append('\n');
            }
            // Trim the trailing newline added by the last "- ...\n" so we don't double-blank-line the marker.
            if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        }

        sb.Append("\n\n<!-- polyphony:plan-child-id=").Append(childId).Append(" -->");
        return sb.ToString();
    }

    private async Task<IReadOnlyList<string>> GetParentTagsAsync(int parentId, CancellationToken ct)
    {
        var item = await twig.ShowAsync(parentId, ct).ConfigureAwait(false);
        var tagsField = item?["tags"]?.GetValue<string>()
            ?? item?["fields"]?["System.Tags"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tagsField)) return Array.Empty<string>();

        return tagsField
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }

    private static void EmitError(string message)
    {
        var result = new PlanSeedChildrenResult
        {
            WorkItemId = 0,
            ChildCount = 0,
            SeededCount = 0,
            ReusedCount = 0,
            ErrorCount = 1,
            SeededItems = Array.Empty<SeedReconciliation>(),
            ReusedItems = Array.Empty<SeedReconciliation>(),
            Errors = new[] { new SeedError { Error = message } },
            Warnings = Array.Empty<string>(),
            PlannedTagSet = false,
            PlannedTagAlready = false,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanSeedChildrenResult));
    }

    private sealed record ChildSnapshot(int Id, string Type, string Title);
}
