using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Guidance;
using Polyphony.Models;
using Polyphony.Policy;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Guidance verbs (<c>polyphony guidance ...</c>). Phase 6 PR #6 — extract
/// the per-item guidance string for a work item per the resolved policy
/// source-of-record. Driver wiring (injecting the extracted text into the
/// agent prompt) lands in Phase 6 PR #5.
/// </summary>
[VerbGroup("guidance")]
public sealed class GuidanceCommands(IWorkItemRepository repository)
{
    /// <summary>
    /// Extracts the per-item guidance for <paramref name="workItem"/> per
    /// the resolved <c>guidance</c> policy. Emits a
    /// <see cref="GuidanceExtractResult"/> with the resolved source, the
    /// extracted text (or null), and a boolean projection.
    /// </summary>
    /// <param name="workItem">ADO work item ID to extract guidance from.</param>
    /// <param name="policy">Path to the policy file. Defaults to
    /// <c>.conductor/policy.yaml</c>.</param>
    [Command("extract")]
    [VerbResult(typeof(GuidanceExtractResult))]
    public async Task<int> Extract(
        int workItem,
        string policy = ".conductor/policy.yaml",
        CancellationToken ct = default)
    {
        var item = await repository.GetByIdAsync(workItem, ct);
        if (item is null)
        {
            Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
            return ExitCodes.CacheError;
        }

        PolicyConfig config;
        try
        {
            config = PolicyLoader.LoadOrDefault(policy);
        }
        catch (Exception ex)
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(ex.Message)}}","work_item_id":{{workItem}}}""");
            return ExitCodes.ConfigError;
        }

        var typeName = item.Type.Value;
        var resolved = PolicyResolver.ResolveGuidance(config, scope: $"type:{typeName}");

        string? extracted;
        try
        {
            extracted = GuidanceExtractor.Extract(item, resolved);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(ex.Message)}}","work_item_id":{{workItem}}}""");
            return ExitCodes.ConfigError;
        }

        var result = new GuidanceExtractResult
        {
            WorkItemId = workItem,
            Source = resolved.Source,
            Guidance = extracted,
            GuidancePresent = extracted is not null,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.GuidanceExtractResult));
        return ExitCodes.Success;
    }

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
