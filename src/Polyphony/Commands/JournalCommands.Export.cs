using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

public sealed partial class JournalCommands
{
    /// <summary>
    /// Export the current root's action journal SQLite file to an arbitrary path.
    /// </summary>
    /// <param name="path">Destination path for the exported SQLite file.</param>
    [Command("journal export")]
    [VerbResult(typeof(JournalExportResult))]
    public async Task<int> Export(string path = "", CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("journal export",
            ("path", string.IsNullOrWhiteSpace(path))) is { } halt)
            return halt;

        try
        {
            var destinationPath = Path.GetFullPath(path);
            await _store.ExportAsync(destinationPath, ct).ConfigureAwait(false);
            var result = new JournalExportResult
            {
                SourcePath = _store.DatabasePath,
                DestinationPath = destinationPath,
                BytesCopied = new FileInfo(destinationPath).Length,
            };
            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.JournalExportResult));
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitError(ex.Message, path);
            return ExitCodes.CacheError;
        }
    }
}
