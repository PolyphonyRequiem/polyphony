using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Tagging;

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

    // Matches the trailing work-item id in a twig-emitted ADO edit URL like
    // https://dev.azure.com/<org>/<project>/_workitems/edit/3072. Used as a
    // fallback when twig new emits id:0 in its JSON payload (observed under
    // an ADO replication race in twig's post-create FetchAsync).
    private static readonly Regex EditUrlIdRegex =
        new(@"/edit/(\d+)(?:[/?#]|$)", RegexOptions.Compiled);

    /// <summary>
    /// Extract the new work-item id from a <c>twig new -o json</c> response.
    /// Tries the structured <c>id</c> field first; falls back to parsing the
    /// id out of the <c>url</c> field when <c>id</c> is missing or zero.
    /// Returns 0 only when both paths fail. <paramref name="source"/> is set
    /// to <c>"id"</c>, <c>"url"</c>, or <c>"none"</c> so the caller can
    /// surface a warning when the fallback path fired.
    /// </summary>
    internal static int ExtractCreatedId(JsonNode created, out string source)
    {
        // Direct id field — happy path.
        var direct = created["id"];
        if (direct is not null)
        {
            try
            {
                var id = direct.GetValue<int>();
                if (id > 0)
                {
                    source = "id";
                    return id;
                }
            }
            catch (InvalidOperationException)
            {
                // Twig emitted id as something other than an int (e.g. string).
                // Don't propagate — fall through to the url-fallback path below
                // so a misbehaving upstream doesn't take the workflow down.
            }
            catch (FormatException)
            {
                // Same reasoning — defensive against future format drift.
            }
        }

        // Fallback: parse the work-item id out of the url field.
        var url = created["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(url))
        {
            var match = EditUrlIdRegex.Match(url);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var urlId) && urlId > 0)
            {
                source = "url";
                return urlId;
            }
        }

        source = "none";
        return 0;
    }

    /// <summary>
    /// Truncate <paramref name="raw"/> to at most <paramref name="max"/>
    /// characters, appending an ellipsis when truncation occurred. Used to
    /// keep self-diagnosing error messages bounded.
    /// </summary>
    internal static string TruncateForError(string raw, int max = 500)
        => raw.Length <= max ? raw : raw[..max] + "…";

    /// <summary>
    /// Idempotently seed the architect's decomposition entries as children of
    /// <paramref name="workItem"/>.
    /// </summary>
    /// <param name="workItem">Parent work item ID.</param>
    /// <param name="childrenJson">JSON array of child objects from <c>architect.output.children</c>.
    /// Each child entry requires <c>child_id</c>, <c>title</c>, <c>type</c>, <c>description</c>.</param>
    /// <param name="plannedTag">Tag value to apply to the parent on success
    /// (defaults to <c>polyphony:planned</c>).</param>
    /// <param name="planFile">Optional plan markdown file to read for
    /// <c>apex_facets</c> front-matter (closed-loop PR #7). When omitted,
    /// defaults to <c>plans/plan-{workItem}.md</c> relative to cwd. Missing
    /// file is treated as "no front-matter declared" and behaviour is
    /// unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("seed-children")]
    [VerbResult(typeof(PlanSeedChildrenResult))]
    public async Task<int> SeedChildren(
        int workItem = RequiredInput.MissingInt,
        string childrenJson = "",
        string plannedTag = "polyphony:planned",
        string planFile = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan seed-children",
            ("--work-item", workItem == RequiredInput.MissingInt),
            ("--children-json", string.IsNullOrEmpty(childrenJson))) is { } halt)
            return halt;

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

        // Read plan-file front-matter for apex_facets (opt-in). Architects
        // declare apex_facets in plans/plan-{id}.md when they choose NOT to
        // decompose ("indivisible apex" — see closed-loop plan §3.4(a)). The
        // resolved facets land on the parent work item as a
        // polyphony:facets=... tag so RequirementInputResolver consumers
        // (worklist build, edges check, next-ready) can override the type-
        // config default on a per-call basis.
        var resolvedPlanFile = string.IsNullOrEmpty(planFile)
            ? Path.Combine("plans", $"plan-{workItem}.md")
            : planFile;
        var apexFacets = ReadApexFacets(resolvedPlanFile, out var apexFacetsError);
        if (apexFacetsError is not null)
        {
            EmitError(apexFacetsError);
            return ExitCodes.ConfigError;
        }

        // apex_facets is mutually exclusive with a non-empty children list.
        // The two say opposite things: "this apex is indivisible" vs "here
        // are its sub-items". A planner that emits both is incoherent; we
        // refuse rather than guess.
        if (apexFacets is { Count: > 0 } && children.Count > 0)
        {
            EmitError(
                $"plan front-matter declares apex_facets ({string.Join(",", apexFacets)}) but children-json contains {children.Count} child entr{(children.Count == 1 ? "y" : "ies")}; the two are mutually exclusive (apex_facets is for indivisible apexes — see closed-loop plan §3.4(a)).");
            return ExitCodes.ConfigError;
        }

        // Indivisibility must be EXPLICIT. An empty children list with no
        // apex_facets declaration is ambiguous: it could mean "this apex is
        // genuinely indivisible" or — more commonly in practice — "the
        // planner declared children in plan body prose but forgot to
        // populate the structured architect.output.children". Silent-tag of
        // zero-children plans is exactly what produced the false-satisfied
        // apex surfaced by the AB#3064 dogfood (2026-05-09): the observer
        // reads only the polyphony:planned tag, the rollup then collapses
        // item_satisfied to satisfied, and the driver short-circuits at
        // preflight having implemented nothing. We refuse rather than guess.
        // To declare indivisibility, the planner must add `apex_facets:` to
        // the plan front-matter.
        if (children.Count == 0 && (apexFacets is null || apexFacets.Count == 0))
        {
            EmitError(
                $"children-json is empty and plan front-matter declares no apex_facets — refusing to stamp #{workItem} as planned. " +
                $"To declare an indivisible apex, add `apex_facets: [<facet>, ...]` to the front-matter of '{resolvedPlanFile}'. " +
                $"Otherwise, supply --children-json containing the architect's structured decomposition.");
            return ExitCodes.ConfigError;
        }

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
                var newId = ExtractCreatedId(created, out var idSource);
                if (newId == 0)
                {
                    // Self-diagnosing error: include the raw twig payload (truncated)
                    // so the next occurrence doesn't require event-log archaeology to
                    // figure out what shape twig actually returned. The dogfood that
                    // motivated this hardening (AB#3071, 2026-05-10) had six children
                    // fail with "twig new returned no id" yet the items WERE created
                    // in ADO — without the raw payload there was no way to tell whether
                    // we got id:0, missing-id, a string id, or some envelope wrapper.
                    var snippet = TruncateForError(created.ToJsonString());
                    errors.Add(new SeedError
                    {
                        ChildId = childId,
                        Title = title,
                        Error = $"twig new returned no usable id (id field and url fallback both failed); raw payload: {snippet}",
                    });
                    continue;
                }
                if (idSource == "url")
                {
                    // Recovered via url fallback — flag it so we know the upstream
                    // twig race is still happening and we shouldn't quietly forget.
                    warnings.Add($"child {childId} → #{newId}: twig new returned id=0 in JSON; recovered id from url field (likely twig fetch-back race in NewCommand.cs)");
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
        var facetsTagSet = false;
        if (errors.Count == 0)
        {
            try
            {
                var currentTagsList = await GetParentTagsAsync(workItem, ct).ConfigureAwait(false);
                var tags = TagSet.Parse(string.Join("; ", currentTagsList));

                var addPlanned = !tags.Contains(plannedTag);
                tagAlreadyPresent = !addPlanned;
                if (addPlanned)
                {
                    tags = tags.Add(plannedTag);
                }

                // Apply the facets-override tag when apex_facets was declared.
                // Replace any existing polyphony:facets=... tag so a re-plan
                // with a different facet set converges, rather than stacking.
                if (apexFacets is { Count: > 0 })
                {
                    var existing = FacetTagParser.TryExtract(tags);
                    if (existing is not null)
                    {
                        // Remove the prior tag verbatim so casing differences
                        // don't survive the round-trip.
                        var prefix = PolyphonyTags.FacetsPrefix + "=";
                        foreach (var t in tags.ToArray())
                        {
                            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                tags = tags.Remove(t);
                            }
                        }
                    }
                    var newFacetsTag = FacetTagParser.FormatTag(apexFacets);
                    tags = tags.Add(newFacetsTag);
                    facetsTagSet = true;
                }

                if (addPlanned || facetsTagSet)
                {
                    await twig.PatchFieldsAsync(workItem,
                        new Dictionary<string, string> { ["System.Tags"] = tags.Format() }, ct).ConfigureAwait(false);
                }
                tagSet = true;
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
            ApexFacets = apexFacets ?? [],
            FacetsTagSet = facetsTagSet,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanSeedChildrenResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Reads the plan markdown file at <paramref name="planFilePath"/> and
    /// returns the architect-declared <c>apex_facets</c> if present and
    /// well-formed. Missing file or absent front-matter return null with no
    /// error (opt-in feature). Malformed front-matter populates
    /// <paramref name="error"/> so the caller can route to the error envelope.
    /// </summary>
    private static IReadOnlyList<string>? ReadApexFacets(string planFilePath, out string? error)
    {
        error = null;
        if (!File.Exists(planFilePath))
        {
            return null;
        }

        string body;
        try
        {
            body = File.ReadAllText(planFilePath);
        }
        catch (Exception ex)
        {
            error = $"failed to read plan file '{planFilePath}': {ex.Message}";
            return null;
        }

        var parsed = PlanFileFrontMatter.Parse(body);
        switch (parsed.Status)
        {
            case PlanFileFrontMatterStatus.Absent:
                return null;
            case PlanFileFrontMatterStatus.Malformed:
                error = $"plan front-matter in '{planFilePath}' is malformed: {parsed.ErrorDetail}";
                return null;
            case PlanFileFrontMatterStatus.Present:
                return parsed.ApexFacets.Count == 0 ? null : parsed.ApexFacets;
            default:
                throw new InvalidOperationException($"Unhandled PlanFileFrontMatterStatus: {parsed.Status}");
        }
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
            ApexFacets = Array.Empty<string>(),
            FacetsTagSet = false,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanSeedChildrenResult));
    }

    private sealed record ChildSnapshot(int Id, string Type, string Title);
}
