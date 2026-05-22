namespace Polyphony.Journal.Payloads;

public sealed record WorktreeAddPayload
{
    public required string Branch { get; init; }
    public required string Path { get; init; }
    public string? GitRef { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
