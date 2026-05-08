using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Postconditions;
using Polyphony.Routing;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Planning-stage verbs (<c>polyphony plan ...</c>). Replaces the deterministic
/// PowerShell scripts invoked from <c>plan-level.yaml</c>:
/// <list type="bullet">
///   <item><c>scripts/depth-guard.ps1</c> → <see cref="DepthGuard"/></item>
///   <item><c>scripts/child-router.ps1</c> → <see cref="NextChild"/></item>
///   <item><c>scripts/load-type-context.ps1</c> → <see cref="LoadType"/></item>
///   <item><c>scripts/load-agent-guidance.ps1</c> → <see cref="LoadGuidance"/></item>
///   <item><c>.conductor/registry/scripts/review-router.ps1</c> → <see cref="Review"/></item>
/// </list>
/// </summary>
/// <remarks>
/// Routing-script verbs (<see cref="DepthGuard"/>, <see cref="NextChild"/>,
/// <see cref="Review"/>) always return <see cref="ExitCodes.Success"/>; the workflow
/// routes on the JSON payload. File-IO verbs (<see cref="LoadType"/>,
/// <see cref="LoadGuidance"/>) follow the standard exit-code convention: success
/// on happy path, non-zero on failure with an <c>error</c> field for the gate
/// prompt to surface.
/// </remarks>
[VerbGroup("plan")]
public sealed partial class PlanCommands(
    HierarchyWalker walker,
    IWorkItemRepository repository,
    ProcessConfig processConfig,
    ITwigClient twig,
    IGitClient git,
    IGhClient gh,
    IPostconditionVerifier postconditions)
{
    /// <summary>
    /// Validates current recursion depth against a configured maximum. Always exits 0.
    /// </summary>
    /// <param name="depth">Current recursion depth (0 = root level).</param>
    /// <param name="maxDepth">Maximum allowed recursion depth.</param>
    [Command("depth-guard")]
    [VerbResult(typeof(PlanDepthGuardResult))]
    public int DepthGuard(int depth = RequiredInput.MissingInt, int maxDepth = 6)
    {
        if (RequiredInput.HaltIfMissing("plan depth-guard",
            ("--depth", depth == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var allowed = depth < maxDepth;
        var remaining = allowed ? maxDepth - depth : 0;
        var message = allowed
            ? $"Depth {depth} is within limit (max {maxDepth}). {remaining} level(s) remaining."
            : $"Recursion depth {depth} reached maximum {maxDepth}";

        var result = new PlanDepthGuardResult
        {
            Allowed = allowed,
            Depth = depth,
            MaxDepth = maxDepth,
            Remaining = remaining,
            Message = message,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanDepthGuardResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Lists immediate children of a work item that have the <c>plannable</c>
    /// facet in the process config. Drives the <c>for_each</c> recursion in
    /// <c>plan-level.yaml</c>. Always exits 0; surfaces any error inline.
    /// </summary>
    /// <param name="workItem">ADO work item ID whose children to discover.</param>
    [Command("next-child")]
    [VerbResult(typeof(PlanNextChildResult))]
    public async Task<int> NextChild(int workItem = RequiredInput.MissingInt, CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan next-child",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var hierarchy = await walker.WalkAsync(workItem, maxDepth: 1, ct);
        if (hierarchy is null)
        {
            // Routing-script contract: always exit 0; surface the error inline,
            // route the workflow to the "no plannable children" branch.
            var notFound = new PlanNextChildResult
            {
                HasPlannableChildren = false,
                PlannableChildren = [],
                ParentId = workItem,
                Count = 0,
                Error = $"Work item {workItem} not found",
            };
            Console.WriteLine(JsonSerializer.Serialize(notFound, PolyphonyJsonContext.Default.PlanNextChildResult));
            return ExitCodes.Success;
        }

        var children = (hierarchy.Children ?? [])
            .Where(c => c.Facets.Contains("plannable"))
            .Select(c => new PlannableChild { Id = c.WorkItemId, Type = c.Type, Title = c.Title })
            .ToArray();

        var result = new PlanNextChildResult
        {
            HasPlannableChildren = children.Length > 0,
            PlannableChildren = children,
            ParentId = workItem,
            Count = children.Length,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanNextChildResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Loads type-specific planning context (definition markdown, optional template,
    /// decomposition guidance) for a work item. Consumed by <c>plan-level.yaml</c>'s
    /// <c>type_loader</c> step. Returns non-zero on missing inputs so the workflow
    /// can route to the type-loader error gate.
    /// </summary>
    /// <param name="workItem">ADO work item ID whose type to load.</param>
    /// <param name="configDir">Conductor config directory containing
    /// <c>work-item-types/&lt;slug&gt;.md</c> and optional templates.</param>
    [Command("load-type")]
    [VerbResult(typeof(PlanLoadTypeResult))]
    public async Task<int> LoadType(int workItem = RequiredInput.MissingInt, string configDir = ".conductor", CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan load-type",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var item = await repository.GetByIdAsync(workItem, ct);
        if (item is null)
        {
            EmitTypeError(string.Empty, $"Work item {workItem} not found");
            return ExitCodes.CacheError;
        }

        var typeName = item.Type.Value;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            EmitTypeError(string.Empty, $"Work item {workItem} has no type field");
            return ExitCodes.CacheError;
        }

        var typeSlug = SlugifyType(typeName);

        var definitionPath = Path.Combine(configDir, "work-item-types", $"{typeSlug}.md");
        if (!File.Exists(definitionPath))
        {
            EmitTypeError(typeName, $"Type definition not found: {definitionPath}");
            return ExitCodes.ConfigError;
        }
        var definition = await File.ReadAllTextAsync(definitionPath, ct);

        var templatePath = Path.Combine(configDir, "work-item-types", "templates", $"{typeSlug}-template.md");
        var template = File.Exists(templatePath)
            ? await File.ReadAllTextAsync(templatePath, ct)
            : string.Empty;

        var decompositionGuidance = string.Empty;
        if (processConfig.Types.TryGetValue(typeName, out var typeConfig))
        {
            decompositionGuidance = (typeConfig.DecompositionGuidance ?? string.Empty).Trim();
        }

        var result = new PlanLoadTypeResult
        {
            Type = typeName,
            Definition = definition,
            Template = template,
            DecompositionGuidance = decompositionGuidance,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanLoadTypeResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Loads agent guidance markdown files into a JSON role map keyed by file
    /// basename (extension stripped). Consumed by <c>plan-level.yaml</c>'s
    /// <c>guidance_loader</c> step and <c>implement-pg.yaml</c>'s equivalent.
    /// Returns an empty object when the guidance directory does not exist —
    /// graceful degradation for repos without agent guidance configured.
    /// </summary>
    /// <param name="configDir">Conductor config directory containing
    /// <c>agent-guidance/*.md</c>.</param>
    [Command("load-guidance")]
    [VerbResult(typeof(Dictionary<string, string>))]
    public int LoadGuidance(string configDir = ".conductor")
    {
        var guidancePath = Path.Combine(configDir, "agent-guidance");
        var roleMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (Directory.Exists(guidancePath))
        {
            // Sort by name so the output is deterministic across platforms.
            var mdFiles = Directory.GetFiles(guidancePath, "*.md", SearchOption.TopDirectoryOnly);
            Array.Sort(mdFiles, StringComparer.Ordinal);

            foreach (var file in mdFiles)
            {
                var roleName = Path.GetFileNameWithoutExtension(file);
                roleMap[roleName] = File.ReadAllText(file);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(roleMap, PolyphonyJsonContext.Default.DictionaryStringString));
        return ExitCodes.Success;
    }

    private static void EmitTypeError(string type, string message)
    {
        var errorResult = new PlanLoadTypeResult
        {
            Type = type,
            Definition = string.Empty,
            Template = string.Empty,
            DecompositionGuidance = string.Empty,
            Error = message,
        };
        Console.WriteLine(JsonSerializer.Serialize(errorResult, PolyphonyJsonContext.Default.PlanLoadTypeResult));
    }

    private static string SlugifyType(string typeName)
    {
        // Match the script: lowercase + collapse runs of whitespace into single dash.
        var lowered = typeName.ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(lowered, @"\s+", "-");
        return slug;
    }

    /// <summary>
    /// Aggregates technical and readability reviewer JSON outputs and decides whether
    /// the plan-level workflow should loop back to the architect or proceed to the
    /// human plan-approval gate. Routing-script convention: always exits
    /// <see cref="ExitCodes.Success"/>; the workflow routes on the <c>passed</c> /
    /// <c>forced_by_cap</c> fields in the JSON payload.
    ///
    /// <para>Pass criteria (any one wins):</para>
    /// <list type="bullet">
    ///   <item><description><c>average_score &gt;= 90</c></description></item>
    ///   <item><description><c>blocking_issue_count == 0</c></description></item>
    ///   <item><description><c>prior_cycle_count &gt;= max-cycles</c> (forced_by_cap=true)</description></item>
    /// </list>
    /// </summary>
    /// <param name="techReviewerJson">Full JSON from the technical_reviewer agent
    /// (must contain <c>score</c> and <c>blocking_issues</c> fields).</param>
    /// <param name="readabilityReviewerJson">Same shape from the readability_reviewer.</param>
    /// <param name="priorCycleCount">Number of times review has already executed in this
    /// workflow run. Computed by the caller from <c>context.history</c>; current
    /// invocation does not count.</param>
    /// <param name="maxCycles">Cycle cap; defaults to 5. When <paramref name="priorCycleCount"/>
    /// reaches or exceeds this, <c>passed=true, forced_by_cap=true</c> is emitted to
    /// escape oscillation. Replaces the hardcoded <c>5</c> at review-router.ps1:79.</param>
    [Command("review")]
    [VerbResult(typeof(PlanReviewResult))]
    public int Review(
        string techReviewerJson = "",
        string readabilityReviewerJson = "",
        int priorCycleCount = RequiredInput.MissingInt,
        int maxCycles = 5)
    {
        if (RequiredInput.HaltIfMissing("plan review",
            ("--tech-reviewer-json", string.IsNullOrEmpty(techReviewerJson)),
            ("--readability-reviewer-json", string.IsNullOrEmpty(readabilityReviewerJson)),
            ("--prior-cycle-count", priorCycleCount == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var (techScore, techBlocking) = ParseReviewerJson(techReviewerJson);
        var (readScore, readBlocking) = ParseReviewerJson(readabilityReviewerJson);

        var blockingCount = techBlocking.Count + readBlocking.Count;
        // [math]::Floor on the sum of two ints is integer division — preserve exactly.
        var avg = (techScore + readScore) / 2;

        var combined = BuildCombinedFeedback(techScore, techBlocking, readScore, readBlocking);

        var passByScore = avg >= 90;
        var passByNoBlocking = blockingCount == 0;
        var capHit = priorCycleCount >= maxCycles;
        var pass = passByScore || passByNoBlocking || capHit;
        var forcedByCap = !(passByScore || passByNoBlocking) && capHit;

        var result = new PlanReviewResult
        {
            AverageScore = avg,
            TechnicalScore = techScore,
            ReadabilityScore = readScore,
            RevisionCyclesCompleted = priorCycleCount,
            BlockingIssueCount = blockingCount,
            CombinedFeedback = combined,
            Passed = pass,
            ForcedByCap = forcedByCap,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanReviewResult));
        return ExitCodes.Success;
    }

    private static (int Score, List<string> BlockingIssues) ParseReviewerJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (0, []);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var score = 0;
            if (root.TryGetProperty("score", out var scoreEl))
            {
                // Match the PowerShell [int] cast — accept either number or string.
                score = scoreEl.ValueKind switch
                {
                    JsonValueKind.Number => scoreEl.GetInt32(),
                    JsonValueKind.String when int.TryParse(scoreEl.GetString(), out var s) => s,
                    _ => 0,
                };
            }

            var blocking = new List<string>();
            if (root.TryGetProperty("blocking_issues", out var blockEl))
            {
                if (blockEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in blockEl.EnumerateArray())
                        blocking.Add(StringifyBlockingItem(item));
                }
                else if (blockEl.ValueKind != JsonValueKind.Null)
                {
                    // The PowerShell To-Array helper wraps a scalar in a single-element array.
                    blocking.Add(StringifyBlockingItem(blockEl));
                }
            }

            return (score, blocking);
        }
        catch (JsonException)
        {
            // Malformed reviewer JSON degrades to "no signal" — same as the script,
            // which would have thrown but the workflow only ever passes valid JSON.
            return (0, []);
        }
    }

    private static string StringifyBlockingItem(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText(),
    };

    private static string BuildCombinedFeedback(
        int techScore, List<string> techBlocking,
        int readScore, List<string> readBlocking)
    {
        if (techBlocking.Count == 0 && readBlocking.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        if (techBlocking.Count > 0)
        {
            sb.AppendLine($"### From technical reviewer (score: {techScore})");
            foreach (var issue in techBlocking)
                sb.AppendLine($"- {issue.Trim()}");
            sb.AppendLine();
        }
        if (readBlocking.Count > 0)
        {
            sb.AppendLine($"### From readability reviewer (score: {readScore})");
            foreach (var issue in readBlocking)
                sb.AppendLine($"- {issue.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }
}

