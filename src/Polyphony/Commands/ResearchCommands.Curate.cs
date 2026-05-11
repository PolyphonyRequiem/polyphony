using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Models;
using Polyphony.Research;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony research curate</c> — the archivist. Enumerates scratch
/// artifacts for the given apex and emits a structured
/// <see cref="ArchivistDecision"/> per artifact.
///
/// <para>The verb reads a JSON array of decisions from
/// <c>--decisions-json</c> (produced by an upstream agent or test fixture)
/// and validates each decision against the schema. The archivist agent
/// itself (#3080) is the LLM step that produces these decisions; this verb
/// is the deterministic CLI envelope that validates and forwards them.</para>
///
/// <para>Routing-style: always exits <see cref="ExitCodes.Success"/>;
/// errors surface in the envelope's <c>error</c> / <c>error_code</c>.</para>
/// </summary>
public sealed partial class ResearchCommands
{
    [Command("curate")]
    [VerbResult(typeof(ResearchCurateResult))]
    public int Curate(
        int apexId = RequiredInput.MissingInt,
        string scratchDir = "",
        string decisionsJson = "")
    {
        if (RequiredInput.HaltIfMissing("research curate",
            ("--apex-id", apexId == RequiredInput.MissingInt),
            ("--scratch-dir", string.IsNullOrEmpty(scratchDir)),
            ("--decisions-json", string.IsNullOrEmpty(decisionsJson))) is { } halt)
            return halt;

        if (!Directory.Exists(scratchDir))
        {
            EmitCurateError(apexId, scratchDir, "scratch_dir_missing",
                $"Scratch directory does not exist: {scratchDir}");
            return ExitCodes.Success;
        }

        List<ArchivistDecision> decisions;
        try
        {
            decisions = JsonSerializer.Deserialize(
                decisionsJson, PolyphonyJsonContext.Default.ListArchivistDecision) ?? [];
        }
        catch (JsonException ex)
        {
            EmitCurateError(apexId, scratchDir, "invalid_decisions_json",
                $"Failed to parse --decisions-json: {ex.Message}");
            return ExitCodes.Success;
        }

        // Validate each decision
        foreach (var d in decisions)
        {
            if (!CurationDecision.IsValid(d.Decision))
            {
                EmitCurateError(apexId, scratchDir, "invalid_decision_value",
                    $"Invalid decision value '{d.Decision}' for artifact '{d.ArtifactPath}'. Must be keep, discard, or expand.");
                return ExitCodes.Success;
            }
        }

        // Cross-check: ensure every scratch file has a decision
        var scratchFiles = Directory.GetFiles(scratchDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(scratchDir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var decidedPaths = decisions.Select(d => d.ArtifactPath).ToHashSet(StringComparer.Ordinal);
        var missing = scratchFiles.Where(f => !decidedPaths.Contains(f)).ToList();
        if (missing.Count > 0)
        {
            EmitCurateError(apexId, scratchDir, "missing_decisions",
                $"No decision for {missing.Count} scratch file(s): {string.Join(", ", missing.Take(5))}");
            return ExitCodes.Success;
        }

        var result = new ResearchCurateResult
        {
            ApexId = apexId,
            ScratchDir = scratchDir,
            Decisions = decisions,
            ArtifactCount = decisions.Count,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResearchCurateResult));
        return ExitCodes.Success;
    }

    private static void EmitCurateError(int apexId, string scratchDir, string errorCode, string error)
    {
        var result = new ResearchCurateResult
        {
            ApexId = apexId,
            ScratchDir = scratchDir,
            Decisions = [],
            ArtifactCount = 0,
            Error = error,
            ErrorCode = errorCode,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResearchCurateResult));
    }
}
