namespace Polyphony.Journal.Payloads;

public sealed record WorktreeRemovePayload
{
    public required string Path { get; init; }
    public required bool Force { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
