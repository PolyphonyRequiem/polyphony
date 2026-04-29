namespace Polyphony;

public sealed record ValidateResult
{
    public required int WorkItemId { get; init; }
    public required string Event { get; init; }
    public required bool IsValid { get; init; }
    public string? Message { get; init; }
    public string? TargetState { get; init; }
}
