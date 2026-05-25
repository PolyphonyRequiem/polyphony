using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

[VerbGroup("")]
public sealed class JournalDriftCommand(
    IJournalReader store,
    IWorkItemRepository repository,
    JournalDriftAnalyzer analyzer)
{
    private readonly IJournalReader _store = store;
    private readonly IWorkItemRepository _repository = repository;
    private readonly JournalDriftAnalyzer _analyzer = analyzer;

    /// <summary>
    /// Diff the journal's expected resource state for a root against the current world.
    /// </summary>
    /// <param name="root">Root work item ID.</param>
    /// <param name="render">Output format: json (default) or text.</param>
    [Command("journal drift")]
    [VerbResult(typeof(DriftResult))]
    public async Task<int> Drift(
        int root = RequiredInput.MissingInt,
        string render = "json",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("journal drift", ("--root", root == RequiredInput.MissingInt)) is { } halt)
        {
            return halt;
        }

        if (root <= 0)
        {
            EmitError("root must be positive");
            return ExitCodes.RoutingFailure;
        }

        try
        {
            var item = await _repository.GetByIdAsync(root, ct).ConfigureAwait(false);
            if (item is null)
            {
                Console.WriteLine($$"""{"error":"Work item {{root}} not found","work_item_id":{{root}}}""");
                return ExitCodes.CacheError;
            }

            var entries = await _store.QueryAsync(new JournalQuery { RootId = root }, ct).ConfigureAwait(false);
            var analysis = await _analyzer.AnalyzeAsync(root, entries, ct).ConfigureAwait(false);
            if (string.Equals(render, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(analysis.Result, PolyphonyJsonContext.Default.DriftResult));
                return ExitCodes.Success;
            }

            if (string.Equals(render, "text", StringComparison.OrdinalIgnoreCase))
            {
                EmitText(analysis.Result);
                return ExitCodes.Success;
            }

            EmitError("render must be 'json' or 'text'");
            return ExitCodes.RoutingFailure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            return ExitCodes.CacheError;
        }
    }

    private static void EmitText(DriftResult result)
    {
        Console.WriteLine($"root\t{result.RootId.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine(
            $"summary\tconsistent={result.Summary.Consistent.ToString(CultureInfo.InvariantCulture)}\texternal_delete={result.Summary.ExternalDelete.ToString(CultureInfo.InvariantCulture)}\texternal_mutation={result.Summary.ExternalMutation.ToString(CultureInfo.InvariantCulture)}\texternal_create={result.Summary.ExternalCreate.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine("classification\tkind\tid\texpected\tactual\towned");
        foreach (var finding in result.Findings)
        {
            Console.WriteLine(string.Join(
                "\t",
                finding.Classification,
                finding.Kind,
                finding.Id,
                finding.ExpectedState,
                finding.ActualState ?? string.Empty,
                finding.PolyphonyOwned?.ToString() ?? string.Empty));
        }
    }

    private static void EmitError(string message)
        => Console.WriteLine($$"""{"error":"{{EscapeJsonString(message)}}"}""");

    private static string EscapeJsonString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
}
