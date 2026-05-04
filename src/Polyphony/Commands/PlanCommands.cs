using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
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
/// </list>
/// </summary>
/// <remarks>
/// Routing-script verbs (<see cref="DepthGuard"/>, <see cref="NextChild"/>) always
/// return <see cref="ExitCodes.Success"/>; the workflow routes on the JSON payload.
/// File-IO verbs (<see cref="LoadType"/>, <see cref="LoadGuidance"/>) follow the
/// standard exit-code convention: success on happy path, non-zero on failure with
/// an <c>error</c> field for the gate prompt to surface.
/// </remarks>
public sealed class PlanCommands(
    HierarchyWalker walker,
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    /// <summary>
    /// Validates current recursion depth against a configured maximum. Always exits 0.
    /// </summary>
    /// <param name="depth">Current recursion depth (0 = root level).</param>
    /// <param name="maxDepth">Maximum allowed recursion depth.</param>
    [Command("depth-guard")]
    public int DepthGuard(int depth, int maxDepth = 6)
    {
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
    /// capability in the process config. Drives the <c>for_each</c> recursion in
    /// <c>plan-level.yaml</c>. Always exits 0; surfaces any error inline.
    /// </summary>
    /// <param name="workItem">ADO work item ID whose children to discover.</param>
    [Command("next-child")]
    public async Task<int> NextChild(int workItem, CancellationToken ct = default)
    {
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
            .Where(c => c.Capabilities.Contains("plannable"))
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
    public async Task<int> LoadType(int workItem, string configDir = ".conductor", CancellationToken ct = default)
    {
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
}
