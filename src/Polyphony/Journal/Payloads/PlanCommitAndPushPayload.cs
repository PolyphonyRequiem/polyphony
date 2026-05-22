namespace Polyphony.Journal.Payloads;

public sealed record PlanCommitAndPushPayload
{
    public required string Branch { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool Pushed { get; init; }
    public required int FilesStaged { get; init; }
    public string? CommitSha { get; init; }
    public string? NoOpReason { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}
