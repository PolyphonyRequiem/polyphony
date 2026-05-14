namespace Polyphony;

/// <summary>
/// JSON output for <c>polyphony reset</c>. Emitted on every invocation —
/// <see cref="Action"/> routes the consumer:
/// <list type="bullet">
///   <item><c>"planned"</c> — dry-run enumeration of artifacts.</item>
///   <item><c>"executed"</c> — reset was performed.</item>
///   <item><c>"needs_confirmation"</c> — neither <c>--dry-run</c> nor <c>--force</c> supplied.</item>
///   <item><c>"error"</c> — hard error (work item not found, git failure).</item>
/// </list>
/// </summary>
public sealed record ResetResult
{
    public required int RootId { get; init; }
    public required string Action { get; init; }
    public bool? DryRun { get; init; }
    public string[]? TagsRemoved { get; init; }
    public int[]? ItemsPatched { get; init; }
    public string[]? LocalBranchesDeleted { get; init; }
    public string[]? RemoteBranchesDeleted { get; init; }
    public string[]? WorktreesRemoved { get; init; }
    public string? StateDir { get; init; }
    public bool? StateDirDeleted { get; init; }
    public int? CommentsArchived { get; init; }
    public string? ArchivePath { get; init; }
    public string? Error { get; init; }
}
