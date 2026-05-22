namespace Polyphony.Journal.Payloads;

public sealed record LockMutationPayload
{
    public required int RootId { get; init; }
    public required string Path { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Reason { get; init; }
    public bool? WasHeld { get; init; }
    public string? Error { get; init; }
}
