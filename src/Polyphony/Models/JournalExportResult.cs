namespace Polyphony;

public sealed record JournalExportResult
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required long BytesCopied { get; init; }
}
