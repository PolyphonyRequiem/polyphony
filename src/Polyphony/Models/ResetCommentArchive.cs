namespace Polyphony;

/// <summary>
/// Sidecar JSON shape written to disk when <c>polyphony reset run</c> archives
/// polyphony-authored comments before clearing them. One archive file per reset
/// invocation, stored under <c>&lt;git-common-dir&gt;/polyphony-archive/</c>.
/// </summary>
public sealed record ResetCommentArchive
{
    public required int RootId { get; init; }
    public required string ArchivedAtUtc { get; init; }
    public required ArchivedWorkItemComments[] Items { get; init; }
}

/// <summary>
/// All archived comments for a single work item within a reset archive.
/// </summary>
public sealed record ArchivedWorkItemComments
{
    public required int WorkItemId { get; init; }
    public required ArchivedComment[] Comments { get; init; }
}

/// <summary>
/// A single archived comment within <see cref="ArchivedWorkItemComments"/>.
/// </summary>
public sealed record ArchivedComment
{
    public required long CommentId { get; init; }
    public required string Text { get; init; }
    public required string CreatedBy { get; init; }
    public string? CreatedDate { get; init; }
}
