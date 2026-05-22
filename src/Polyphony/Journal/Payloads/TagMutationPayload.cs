namespace Polyphony.Journal.Payloads;

public sealed record TagMutationPayload
{
    public required int WorkItemId { get; init; }
    public required string Tag { get; init; }
    public required bool EnsurePresent { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool AlreadyInDesiredState { get; init; }
    public required string[] TagsBefore { get; init; }
    public required string[] TagsAfter { get; init; }
    public string? Error { get; init; }
}
