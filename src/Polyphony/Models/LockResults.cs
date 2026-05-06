using System.Text.Json.Serialization;
using Polyphony.Locking;

namespace Polyphony.Models;

public sealed record AcquireLockResult
{
    public required string Path { get; init; }
    public required bool Acquired { get; init; }
    public string? Reason { get; init; }
    public string? Hint { get; init; }
    public string? LockToken { get; init; }
    public RunLock? Lock { get; init; }
    public RunLock? ExistingLock { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record ReleaseLockResult
{
    public required string Path { get; init; }
    public required bool Released { get; init; }
    public string? Reason { get; init; }
    public RunLock? ExistingLock { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record ForceReleaseLockResult
{
    public required string Path { get; init; }
    public required bool Released { get; init; }
    public required bool WasHeld { get; init; }
    public RunLock? ExistingLock { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record LockStatusResult
{
    public required string Path { get; init; }
    public required bool Exists { get; init; }
    public required bool Valid { get; init; }
    public required bool Stale { get; init; }
    public RunLock? Lock { get; init; }
    public long? TtlRemainingSeconds { get; init; }
    public string? ParseError { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
