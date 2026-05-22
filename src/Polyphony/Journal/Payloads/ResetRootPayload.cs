namespace Polyphony.Journal.Payloads;

public sealed record ResetRootPayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required IReadOnlyList<string> StepsCompleted { get; init; }
    public required IReadOnlyList<string> StepsFailed { get; init; }
    public required bool StateSkipped { get; init; }
    public string? Error { get; init; }
}
