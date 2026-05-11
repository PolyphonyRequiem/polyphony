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

    // Pattern: twig new -o json sometimes returns ONLY a `message` field
    // (e.g. `{"message":"Created #3080 Foo (Task)"}`) with no structured
    // `id` or `url`. We extract the `#NNNN` from the message text as a
    // last-resort fallback so the seeder doesn't fail in this case.
    // Observed in dogfood AB#3075 (2026-05-11) — the items were created
    // in ADO but the JSON had no machine-readable id field.
    private static readonly Regex MessageCreatedIdRegex =
        new(@"\bCreated\s+#(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extract the new work-item id from a <c>twig new -o json</c> response.
    /// Tries the structured <c>id</c> field first; falls back to parsing the
    /// id out of the <c>url</c> field; falls back again to parsing
    /// <c>"Created #NNNN"</c> out of the <c>message</c> field. Returns 0
    /// only when all three paths fail. <paramref name="source"/> is set to
    /// <c>"id"</c>, <c>"url"</c>, <c>"message"</c>, or <c>"none"</c> so
    /// the caller can surface a warning when a fallback path fired.
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

        // Fallback 1: parse the work-item id out of the url field.
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

        // Fallback 2: parse "Created #NNNN" out of the message field.
        // Some twig code paths emit only a human-readable message and no
        // structured id/url (observed AB#3075 dogfood). The work item was
        // genuinely created on ADO; we just need the id.
        var message = created["message"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(message))
        {
            var match = MessageCreatedIdRegex.Match(message);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var msgId) && msgId > 0)
            {
                source = "message";
                return msgId;
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
    /// <param name="configDir">Polyphony config directory containing
    /// <c>work-item-types/templates/&lt;typeslug&gt;-template.md</c> files.
    /// When a template exists for the child's type, it's applied as the
    /// description scaffold (architect's free-form description fills the
    /// first narrative section; architect's <c>acceptance_criteria</c> fill
    /// the AC section; other template placeholders pass through as TODOs
    /// for the implementer). When no template exists, the previous
    /// behaviour stands (architect's description verbatim + AC + marker).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("seed-children")]
    [VerbResult(typeof(PlanSeedChildrenResult))]
    public async Task<int> SeedChildren(
        int workItem = RequiredInput.MissingInt,
        string childrenJson = "",
        string plannedTag = "polyphony:planned",
        string planFile = "",
        string configDir = ".polyphony-config",
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

                var description = BuildDescription(child, childId, configDir);
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
                        Error = $"twig new returned no usable id (id field, url fallback, and message-text fallback all failed); raw payload: {snippet}",
                    });
                    continue;
                }
                if (idSource == "url")
                {
                    // Recovered via url fallback — flag it so we know the upstream
                    // twig race is still happening and we shouldn't quietly forget.
                    warnings.Add($"child {childId} → #{newId}: twig new returned id=0 in JSON; recovered id from url field (likely twig fetch-back race in NewCommand.cs)");
                }
                else if (idSource == "message")
                {
                    // Recovered via message-text fallback — twig returned only a
                    // human-readable "Created #NNNN" line with no structured id
                    // or url. Less reliable than the url fallback (depends on
                    // twig's message phrasing) but better than failing closed.
                    warnings.Add($"child {childId} → #{newId}: twig new returned only a 'message' field; recovered id by parsing 'Created #N' (observed AB#3075 dogfood)");
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

    private static string BuildDescription(JsonObject child, string childId, string configDir)
    {
        var body = child["description"]?.GetValue<string>()?.TrimEnd() ?? "";
        var ac = child["acceptance_criteria"] is JsonArray acArr
            ? acArr.Select(c => c?.GetValue<string>() ?? "")
                   .Where(s => !string.IsNullOrWhiteSpace(s))
                   .ToList()
            : new List<string>();

        var type = child["type"]?.GetValue<string>() ?? "";
        var template = TryLoadTypeTemplate(configDir, type);

        var content = template is null
            ? RenderWithoutTemplate(body, ac)
            : ApplyTypeTemplate(template, body, ac);

        return content + "\n\n<!-- polyphony:plan-child-id=" + childId + " -->";
    }

    private static string RenderWithoutTemplate(string body, IReadOnlyList<string> ac)
    {
        var sb = new System.Text.StringBuilder(body);
        if (ac.Count > 0)
        {
            sb.Append("\n\n## Acceptance Criteria\n");
            foreach (var c in ac) sb.Append("- ").Append(c).Append('\n');
            if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read <c>{configDir}/work-item-types/templates/{typeslug}-template.md</c>
    /// if present. Returns <c>null</c> when the directory is absent, the file
    /// does not exist, or any IO error occurs — the seeder degrades to the
    /// no-template behaviour rather than failing the whole seed.
    /// </summary>
    internal static string? TryLoadTypeTemplate(string configDir, string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(configDir))
            return null;
        var slug = SlugifyTypeForTemplate(typeName);
        var path = Path.Combine(configDir, "work-item-types", "templates", $"{slug}-template.md");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string SlugifyTypeForTemplate(string typeName)
        => Regex.Replace(typeName.ToLowerInvariant(), @"\s+", "-");

    /// <summary>
    /// Apply a type template as the description scaffold, slotting in the
    /// architect's free-form <paramref name="body"/> (into the first
    /// narrative section's placeholder line) and <paramref name="ac"/>
    /// (replacing the placeholder lines under <c>## Acceptance Criteria</c>,
    /// preserving the template's hardcoded items like "Build passes" /
    /// "tests pass"). Other template placeholders (<c>&lt;...&gt;</c>) pass
    /// through as TODOs for the implementer.
    /// </summary>
    /// <remarks>
    /// Idempotency: only the first <c>&lt;...&gt;</c> placeholder body of the
    /// first narrative section is replaced — re-rendering with the same
    /// inputs produces the same output. Architect content is never silently
    /// dropped: if there's no narrative placeholder to replace (template
    /// hand-edited), architect's body is appended at the top under an
    /// <c>## Architect Notes</c> heading instead.
    /// </remarks>
    internal static string ApplyTypeTemplate(string template, string body, IReadOnlyList<string> ac)
    {
        // Normalise template line endings to LF for processing.
        var normalised = template.Replace("\r\n", "\n");
        var lines = normalised.Split('\n').ToList();

        // 1) Slot architect body into the first narrative section's
        //    placeholder line. A "narrative placeholder" is a single line
        //    starting with `<` that follows a `## Header` (other than
        //    Acceptance Criteria, which is handled separately because its
        //    placeholders are list items).
        var bodySlotted = false;
        if (!string.IsNullOrWhiteSpace(body))
        {
            for (int i = 0; i < lines.Count - 1 && !bodySlotted; i++)
            {
                var header = lines[i];
                if (!header.StartsWith("## ")) continue;
                if (header.Contains("Acceptance Criteria", StringComparison.OrdinalIgnoreCase)) continue;

                // Find the first non-blank line in the section.
                int j = i + 1;
                while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
                if (j >= lines.Count) continue;
                if (lines[j].StartsWith("## ")) continue;

                // Only replace if it looks like a placeholder (single `<...>` chunk).
                var trimmed = lines[j].TrimStart();
                if (!trimmed.StartsWith("<")) continue;

                // Find the end of the contiguous placeholder block (consecutive
                // non-blank, non-header lines that are part of the angle-bracket
                // chunk — handles multi-line `<...>` placeholders).
                int end = j;
                while (end + 1 < lines.Count
                       && !string.IsNullOrWhiteSpace(lines[end + 1])
                       && !lines[end + 1].StartsWith("## ")
                       && !lines[end + 1].StartsWith("- "))
                {
                    end++;
                }

                var replacement = body.Replace("\r\n", "\n").TrimEnd().Split('\n');
                lines.RemoveRange(j, end - j + 1);
                lines.InsertRange(j, replacement);
                bodySlotted = true;
            }

            if (!bodySlotted)
            {
                // No narrative placeholder available — prepend an Architect
                // Notes section at the top so architect content is never
                // silently dropped.
                lines.InsertRange(0, new[]
                {
                    "## Architect Notes",
                    "",
                    body.Replace("\r\n", "\n").TrimEnd(),
                    "",
                });
            }
        }

        // 2) Slot architect AC into the `## Acceptance Criteria` section.
        //    Replace each `- [ ] <...>` placeholder line with `- [ ] {item}`.
        //    Preserve hardcoded items (those without `<>` markers) — typically
        //    "Build passes" / "Tests pass" boilerplate the template author
        //    wants on every child of this type.
        if (ac.Count > 0)
        {
            int acHeader = lines.FindIndex(l => l.StartsWith("## ") && l.Contains("Acceptance Criteria", StringComparison.OrdinalIgnoreCase));
            if (acHeader >= 0)
            {
                int sectionEnd = acHeader + 1;
                while (sectionEnd < lines.Count && !lines[sectionEnd].StartsWith("## ")) sectionEnd++;

                var preserved = new List<string>();
                var hadPlaceholder = false;
                for (int i = acHeader + 1; i < sectionEnd; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("- [ ]") || trimmed.StartsWith("- ["))
                    {
                        if (line.Contains('<') && line.Contains('>'))
                        {
                            // Placeholder list item — drop, will be replaced.
                            hadPlaceholder = true;
                            continue;
                        }
                        preserved.Add(line);
                    }
                }

                var rebuilt = new List<string> { "" };
                foreach (var c in ac) rebuilt.Add($"- [ ] {c}");
                if (preserved.Count > 0)
                {
                    rebuilt.AddRange(preserved);
                }
                rebuilt.Add("");
                _ = hadPlaceholder;

                lines.RemoveRange(acHeader + 1, sectionEnd - acHeader - 1);
                lines.InsertRange(acHeader + 1, rebuilt);
            }
            else
            {
                // No AC section in template — append one at the bottom.
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
                lines.Add("");
                lines.Add("## Acceptance Criteria");
                lines.Add("");
                foreach (var c in ac) lines.Add($"- [ ] {c}");
            }
        }

        // Trim trailing blank lines so the marker sits cleanly at the bottom.
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
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
