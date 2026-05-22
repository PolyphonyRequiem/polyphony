namespace Polyphony.Journal.Payloads;

public sealed record WorktreeCreatePayload
{
    public required int RootId { get; init; }
    public string? Branch { get; init; }
    public string? Slug { get; init; }
    public string? Ref { get; init; }
    public string? RootRoot { get; init; }
    public string? WorktreePath { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public string? Error { get; init; }
}
