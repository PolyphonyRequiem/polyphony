using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony agent archivist</c> — enumerates scratch artifacts for an
/// apex and emits a per-artifact decision envelope. The archivist is the
/// curation gate at the end of a research cycle: it walks
/// <c>research/scratch/{apex}/</c>, collects relative file paths in a
/// deterministic (ordinal-sorted) order, and outputs an
/// <see cref="ArchivistResult"/> whose <see cref="ArchivistResult.Decisions"/>
/// array is populated by the calling workflow's LLM agent step.
///
/// <para>This verb owns the enumeration + envelope shape; the actual
/// keep/discard/expand decisions are authored by the LLM and validated
/// against the <see cref="ArchivistDecision"/> schema downstream.</para>
///
/// <para>Routing-style verb: ALWAYS exits <see cref="ExitCodes.Success"/>;
/// the workflow gates on the envelope's <c>error_code</c>.</para>
/// </summary>
public sealed partial class AgentCommands
{
    /// <summary>
    /// Enumerate scratch artifacts for <paramref name="apex"/> and emit the
    /// archivist result envelope.
    /// </summary>
    /// <param name="apex">Apex work item ID whose scratch directory to
    /// enumerate.</param>
    /// <param name="scratchRoot">Root directory containing per-apex scratch
    /// folders. Defaults to <c>research/scratch</c>.</param>
    [Command("archivist")]
    [VerbResult(typeof(ArchivistResult))]
    public Task<int> Archivist(
        [Argument] int apex = RequiredInput.MissingInt,
        string scratchRoot = "research/scratch")
    {
        if (RequiredInput.HaltIfMissing("agent archivist",
            ("apex", apex == RequiredInput.MissingInt)) is { } halt)
            return Task.FromResult(halt);

        if (apex <= 0)
        {
            Emit(EmptyArchivistResult(apex, "", "apex must be positive", "invalid_argument"));
            return Task.FromResult(ExitCodes.Success);
        }

        var scratchDir = Path.Combine(scratchRoot, apex.ToString());

        if (!Directory.Exists(scratchDir))
        {
            Emit(EmptyArchivistResult(apex, scratchDir,
                $"Scratch directory not found: {scratchDir}", "scratch_dir_not_found"));
            return Task.FromResult(ExitCodes.Success);
        }

        var artifacts = Directory.EnumerateFiles(scratchDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(scratchDir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (artifacts.Count == 0)
        {
            Emit(EmptyArchivistResult(apex, scratchDir,
                "No artifacts found in scratch directory.", "no_artifacts"));
            return Task.FromResult(ExitCodes.Success);
        }

        var result = new ArchivistResult
        {
            Apex = apex,
            ScratchPath = scratchDir,
            Decisions = artifacts.Select(a => new ArchivistDecision
            {
                Artifact = a,
                Decision = "",
                Rationale = "",
                RelevanceSignals = new RelevanceSignals
                {
                    Domain = "",
                    Codebase = "",
                    TechnologyStacks = "",
                    Ecosystem = "",
                    Linkability = "",
                },
            }).ToList(),
        };

        Emit(result);
        return Task.FromResult(ExitCodes.Success);
    }

    private static ArchivistResult EmptyArchivistResult(
        int apex, string scratchPath, string error, string errorCode) =>
        new()
        {
            Apex = apex,
            ScratchPath = scratchPath,
            Decisions = [],
            Error = error,
            ErrorCode = errorCode,
        };

    private static void Emit(ArchivistResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.ArchivistResult));
}
