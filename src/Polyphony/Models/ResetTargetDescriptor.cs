namespace Polyphony;

public sealed record ResetTargetDescriptor
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Operation { get; init; }
}

public sealed record FailedResetTargetDescriptor
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public required string Error { get; init; }
}
