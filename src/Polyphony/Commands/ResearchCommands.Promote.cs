using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Models;
using Polyphony.Research;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony research promote</c> — the promotion writer. Reads
/// archivist decisions and writes <c>keep</c> artifacts to the sibling
/// research repo through <see cref="IResearchStore"/>. <c>discard</c>
/// decisions are no-ops; <c>expand</c> decisions are recorded in the
/// result for the #3076 loop-back.
///
/// <para>Citation enrichment: each promoted artifact carries
/// <see cref="CitationMetadata"/> (source URL, capture date, freshness)
/// prepended as YAML front matter.</para>
///
/// <para>Routing-style: always exits <see cref="ExitCodes.Success"/>;
/// errors surface in the envelope's <c>error</c> / <c>error_code</c>.</para>
/// </summary>
public sealed partial class ResearchCommands
{
    [Command("promote")]
    [VerbResult(typeof(ResearchPromoteResult))]
    public async Task<int> Promote(
        int apexId = RequiredInput.MissingInt,
        string scratchDir = "",
        string decisionsJson = "",
        string destinationJson = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("research promote",
            ("--apex-id", apexId == RequiredInput.MissingInt),
            ("--scratch-dir", string.IsNullOrEmpty(scratchDir)),
            ("--decisions-json", string.IsNullOrEmpty(decisionsJson)),
            ("--destination-json", string.IsNullOrEmpty(destinationJson))) is { } halt)
            return halt;

        List<ArchivistDecision> decisions;
        try
        {
            decisions = JsonSerializer.Deserialize(
                decisionsJson, PolyphonyJsonContext.Default.ListArchivistDecision) ?? [];
        }
        catch (JsonException ex)
        {
            EmitPromoteError(apexId, "invalid_decisions_json",
                $"Failed to parse --decisions-json: {ex.Message}");
            return ExitCodes.Success;
        }

        ResearchDestination destination;
        try
        {
            destination = JsonSerializer.Deserialize(
                destinationJson, PolyphonyJsonContext.Default.ResearchDestination)
                ?? throw new JsonException("Deserialized to null");
        }
        catch (JsonException ex)
        {
            EmitPromoteError(apexId, "invalid_destination_json",
                $"Failed to parse --destination-json: {ex.Message}");
            return ExitCodes.Success;
        }

        var promoted = new List<string>();
        var expandRequested = new List<string>();
        var discardedCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var decision in decisions)
        {
            switch (decision.Decision)
            {
                case CurationDecision.Keep:
                {
                    var scratchPath = Path.Combine(scratchDir, decision.ArtifactPath);
                    if (!File.Exists(scratchPath))
                    {
                        EmitPromoteError(apexId, "scratch_file_missing",
                            $"Scratch file not found: {scratchPath}");
                        return ExitCodes.Success;
                    }

                    var rawContent = await File.ReadAllTextAsync(scratchPath, ct).ConfigureAwait(false);

                    var citation = ExtractCitation(rawContent, now);
                    var enrichedContent = PrependCitationFrontMatter(rawContent, citation);

                    var writeResult = await _researchStore.WriteAsync(
                        destination,
                        decision.ArtifactPath,
                        enrichedContent,
                        $"Promote: {decision.ArtifactPath} (apex {apexId})",
                        ct).ConfigureAwait(false);

                    if (writeResult.Outcome == ResearchWriteResult.Outcomes.Failed)
                    {
                        EmitPromoteError(apexId, "write_failed",
                            $"Failed to write '{decision.ArtifactPath}': {writeResult.Error}");
                        return ExitCodes.Success;
                    }

                    promoted.Add(decision.ArtifactPath);
                    break;
                }

                case CurationDecision.Discard:
                    discardedCount++;
                    break;

                case CurationDecision.Expand:
                    expandRequested.Add(decision.ArtifactPath);
                    break;

                default:
                    EmitPromoteError(apexId, "invalid_decision_value",
                        $"Invalid decision value '{decision.Decision}' for artifact '{decision.ArtifactPath}'.");
                    return ExitCodes.Success;
            }
        }

        var platformCombo = $"source:github+research:{destination.Platform}";

        var result = new ResearchPromoteResult
        {
            ApexId = apexId,
            Promoted = promoted,
            ExpandRequested = expandRequested,
            DiscardedCount = discardedCount,
            PlatformCombo = platformCombo,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResearchPromoteResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Extracts citation metadata from raw artifact content. Looks for a
    /// <c>source_url:</c> line in existing front matter; falls back to
    /// <c>"unknown"</c> when absent.
    /// </summary>
    internal static CitationMetadata ExtractCitation(string rawContent, DateTimeOffset now)
    {
        var sourceUrl = "unknown";
        var captureDate = now;

        // Try to parse existing YAML front matter for source_url / capture_date
        if (rawContent.StartsWith("---", StringComparison.Ordinal))
        {
            var endIndex = rawContent.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIndex > 0)
            {
                var frontMatter = rawContent[3..endIndex];
                foreach (var line in frontMatter.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("source_url:", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceUrl = trimmed["source_url:".Length..].Trim().Trim('"', '\'');
                    }
                    else if (trimmed.StartsWith("capture_date:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTimeOffset.TryParse(trimmed["capture_date:".Length..].Trim().Trim('"', '\''), out var parsed))
                        {
                            captureDate = parsed;
                        }
                    }
                }
            }
        }

        return new CitationMetadata
        {
            SourceUrl = sourceUrl,
            CaptureDate = captureDate.ToString("o"),
            Freshness = CitationMetadata.ComputeFreshness(captureDate, now),
        };
    }

    /// <summary>
    /// Prepends (or replaces) YAML front matter with citation metadata.
    /// </summary>
    internal static string PrependCitationFrontMatter(string content, CitationMetadata citation)
    {
        // Strip existing front matter if present
        var body = content;
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIndex > 0)
            {
                body = content[(endIndex + 4)..].TrimStart('\r', '\n');
            }
        }

        return $"---\nsource_url: \"{citation.SourceUrl}\"\ncapture_date: \"{citation.CaptureDate}\"\nfreshness: \"{citation.Freshness}\"\n---\n{body}";
    }

    private static void EmitPromoteError(int apexId, string errorCode, string error)
    {
        var result = new ResearchPromoteResult
        {
            ApexId = apexId,
            Promoted = [],
            ExpandRequested = [],
            DiscardedCount = 0,
            PlatformCombo = string.Empty,
            Error = error,
            ErrorCode = errorCode,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResearchPromoteResult));
    }
}
