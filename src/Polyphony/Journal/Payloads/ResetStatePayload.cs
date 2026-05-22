namespace Polyphony.Journal.Payloads;

public sealed record ResetStatePayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? PreviousWatermark { get; init; }
    public string? NewWatermark { get; init; }
    public required int RemovedDuplicateTags { get; init; }
    public string? Error { get; init; }
}
