using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Research;
using Polyphony.Models;

namespace Polyphony.Commands;

/// <summary>
/// Research verbs (<c>polyphony research ...</c>). Phase 4 of Epic 3131 —
/// archivist curation pass and sibling-repo write.
///
/// <list type="bullet">
///   <item><c>write-articles</c> — writes archivist-curated articles to the
///   sibling research repository with Johnny-Decimal layout, YAML frontmatter,
///   and INDEX.md maintenance.</item>
/// </list>
/// </summary>
[VerbGroup("research")]
public sealed class ResearchCommands(IResearchStorage storage)
{
    /// <summary>
    /// Reads the archivist's curation output and writes kept articles to the
    /// sibling research repository. Each article receives a deterministically
    /// allocated Johnny-Decimal number, YAML frontmatter, and an INDEX.md entry.
    /// </summary>
    /// <param name="inputFile">
    /// Path to a JSON file containing the <see cref="ArchivistOutput"/>
    /// payload, or <c>-</c> to read from stdin. Ignored when
    /// <paramref name="inputJson"/> is supplied.
    /// </param>
    /// <param name="inputJson">
    /// Inline JSON payload (<see cref="ArchivistOutput"/>). When supplied,
    /// takes precedence over <paramref name="inputFile"/>. Used by conductor
    /// workflow script steps which cannot pipe to stdin (per the workflow
    /// schema's prohibition on a <c>stdin:</c> field on <c>type: script</c>
    /// steps) — JSON is templated inline as an arg.
    /// </param>
    /// <param name="workItem">ADO work item ID that triggered the research.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("write-articles")]
    [VerbResult(typeof(ResearchWriteArticlesResult))]
    public async Task<int> WriteArticles(
        string inputFile = "-",
        string inputJson = "",
        int workItem = RequiredInput.MissingInt,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("research write-articles",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        ArchivistOutput? output;
        try
        {
            string json;
            if (!string.IsNullOrEmpty(inputJson))
            {
                json = inputJson;
            }
            else if (inputFile == "-")
            {
                json = await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            else
            {
                json = await File.ReadAllTextAsync(inputFile, ct).ConfigureAwait(false);
            }

            output = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ArchivistOutput);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($$"""{"error":"Failed to read archivist output: {{EscapeJsonString(ex.Message)}}"}""");
            return ExitCodes.ConfigError;
        }

        if (output is null)
        {
            Console.WriteLine("""{"error":"Archivist output deserialized to null"}""");
            return ExitCodes.ConfigError;
        }

        try
        {
            var writer = new ResearchArticleWriter(storage);
            var result = await writer.WriteArticlesAsync(output, workItem, ct).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(
                result, PolyphonyJsonContext.Default.ResearchWriteArticlesResult));
            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not configured"))
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(ex.Message)}}"}""");
            return ExitCodes.ConfigError;
        }
    }

    private static string EscapeJsonString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
