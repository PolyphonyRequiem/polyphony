namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr get-comments-ado</c> — harvest the human-authored
/// review comments on an Azure DevOps pull request. Closes the comment-text
/// gap noted in <c>docs/decisions/ado-feature-pr-parity.md</c>: prior to this
/// verb the ADO branch of <c>feature-pr.yaml</c>'s remediation planner had to
/// work from reviewer identity + vote alone because no verb returned thread
/// or comment text.
///
/// <para>Always exits 0 (routing-style verb). Failure modes surface in the
/// <see cref="Error"/> + <see cref="ErrorCode"/> fields of the JSON envelope
/// rather than via process exit codes — matches the
/// <see cref="PrPostCommentAdoResult"/> / <see cref="PrPollStatusResult"/>
/// envelope shape so downstream YAMLs can branch on the same field names.</para>
///
/// <para>Comments are flattened from ADO's thread → comment hierarchy: the
/// thread's <c>id</c>, status, and code position (file path + line) are
/// denormalised onto every comment row so consumers don't need to reason
/// about the parent thread separately. System-generated comments
/// (<c>commentType == "system"</c>) and tombstoned comments
/// (<c>isDeleted == true</c>) are filtered out before flattening.</para>
///
/// <para>Snake-case via the global <see cref="PolyphonyJsonContext"/>
/// JsonSerializerOptions.</para>
/// </summary>
public sealed record PrGetCommentsAdoResult
{
    /// <summary>PR number the comments were harvested from (echoed for traceability).</summary>
    public required int PrNumber { get; init; }

    /// <summary>org/project/repo slug; empty when arguments failed validation.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>
    /// Canonical PR URL (<c>https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}</c>).
    /// Empty when arguments failed validation.
    /// </summary>
    public required string PrUrl { get; init; }

    /// <summary>Number of comments returned in <see cref="Comments"/> after filtering.</summary>
    public required int Count { get; init; }

    /// <summary>
    /// Flattened comment list — one row per author-authored comment in the
    /// PR. Order matches ADO's thread order, then per-thread comment order
    /// (oldest first).
    /// </summary>
    public required IReadOnlyList<AdoPrComment> Comments { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Stable machine-readable error code. Vocabulary:
    /// <list type="bullet">
    ///   <item><c>invalid_argument</c> — required arg missing or non-positive PR number.</item>
    ///   <item><c>pr_not_found</c> — ADO returned 404 (PR or repo not found).</item>
    ///   <item><c>ado_timeout</c> — every retry attempt timed out.</item>
    ///   <item><c>ado_failed</c> — non-success HTTP response (5xx or other) or unexpected error.</item>
    ///   <item><c>no_pat</c> — no PAT configured, or PAT rejected (401/403).</item>
    /// </list>
    /// Omitted on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Single per-comment row inside <see cref="PrGetCommentsAdoResult.Comments"/>.
/// Flattened from ADO's thread → comment hierarchy so each row carries both
/// the comment-level fields (id, author, body, timestamps) and the
/// thread-level context (thread id, status, code position).
/// </summary>
public sealed record AdoPrComment
{
    /// <summary>Comment ID (unique within the thread).</summary>
    public required int Id { get; init; }

    /// <summary>Parent thread ID; the same value is shared across all comments in one ADO discussion thread.</summary>
    public required int ThreadId { get; init; }

    /// <summary>
    /// Parent comment ID for replies; <c>0</c> when the comment is the
    /// top-level (first) comment in its thread.
    /// </summary>
    public required int ParentCommentId { get; init; }

    /// <summary>
    /// Author display name. Falls back to the unique name (typically email)
    /// when the display name is missing. Empty string when ADO surfaced
    /// neither (system / anonymous edge cases).
    /// </summary>
    public required string Author { get; init; }

    /// <summary>Comment body (Markdown). Empty when ADO returned null content.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// File path the thread is anchored to (e.g. <c>/src/Foo.cs</c>); null
    /// when the thread is a top-level PR comment with no code position.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// 1-based line number on the right (post-change) side of the diff;
    /// null when the thread is a top-level PR comment or anchored only to
    /// the left (pre-change) side.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// UTC timestamp the comment was first published. Always present in
    /// practice but typed nullable to match ADO's wire shape.
    /// </summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent edit; equals
    /// <see cref="PublishedAt"/> when the comment has not been edited.
    /// </summary>
    public DateTime? LastUpdatedAt { get; init; }

    /// <summary>
    /// True when the comment lives in a thread whose ADO
    /// <c>CommentThreadStatus</c> is <c>fixed</c>, <c>wontFix</c>,
    /// <c>closed</c>, or <c>byDesign</c>. Top-level <c>active</c> /
    /// <c>pending</c> threads (and the rare <c>unknown</c>) are not
    /// resolved.
    /// </summary>
    public required bool IsResolved { get; init; }

    /// <summary>
    /// True when the comment's anchor no longer points at live code in
    /// the latest PR iteration. Always <c>false</c> for now — ADO does
    /// not expose a direct equivalent of GitHub's
    /// <c>position == null</c> outdated signal in the threads payload;
    /// surfacing it would require correlating thread iteration context
    /// against the PR's latest iteration ID. The field is present for
    /// shape parity with the GitHub equivalent verb (so consumers can
    /// branch on the same field name on either platform).
    /// </summary>
    public required bool IsOutdated { get; init; }

    /// <summary>
    /// Thread status from ADO's <c>CommentThreadStatus</c> enum, lowercased:
    /// <c>active | pending | fixed | wontFix | closed | byDesign | unknown</c>.
    /// Echoed verbatim so consumers can apply richer policies than the
    /// boolean <see cref="IsResolved"/> projection.
    /// </summary>
    public required string ThreadStatus { get; init; }

    /// <summary>
    /// Comment type from ADO's <c>CommentType</c> enum, lowercased:
    /// typically <c>text</c> for human-authored comments. <c>system</c>
    /// comments are filtered before flattening so they do not appear here,
    /// but the field is preserved for the rare <c>codeChange</c> case and
    /// for consumer auditability.
    /// </summary>
    public required string CommentType { get; init; }
}
