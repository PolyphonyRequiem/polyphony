namespace Polyphony;

public sealed record ValidateResult
{
    public required int WorkItemId { get; init; }
    public required string Event { get; init; }
    public required bool IsValid { get; init; }
    public string? Message { get; init; }
    public string? TargetState { get; init; }

    /// <summary>
    /// True when the event is a structural no-op because the item is already
    /// in <see cref="TargetState"/>. Null (omitted from JSON) for genuine
    /// transitions and invalid events. AB#3170: lets terminal-event sites
    /// distinguish "applied" from "already done" without try/catch.
    /// </summary>
    public bool? NoOp { get; init; }
}
