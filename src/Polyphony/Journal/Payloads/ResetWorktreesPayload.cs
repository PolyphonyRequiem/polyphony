namespace Polyphony.Journal.Payloads;

public sealed record ResetWorktreesPayload
{
    public required int Root { get; init; }
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required string RootRunsRoot { get; init; }
    public required IReadOnlyList<ResetRemovedWorktree> RemovedWorktrees { get; init; }
    public required IReadOnlyList<ResetFailedWorktree> FailedWorktrees { get; init; }
    public required bool RootDirDeleted { get; init; }
    public string? Error { get; init; }
}
