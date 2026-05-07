namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr post-comment-ado</c> — the ADO equivalent of
/// <c>gh pr review {prNumber} --comment --body "&lt;body&gt;"</c>. Records the
/// comment that was posted (or attempted) and surfaces a stable
/// <see cref="ErrorCode"/> the workflow YAML can branch on.
///
/// <para>The verb always exits 0 (routing-style); failure modes surface in
/// the envelope. Successful invocations have <see cref="Posted"/> = true,
/// <see cref="ThreadId"/> + <see cref="CommentId"/> populated from ADO's
/// response, <see cref="Error"/> = null, <see cref="ErrorCode"/> = null.</para>
///
/// <para>The advisory comment is posted as a closed thread (status: 4) — a
/// single top-level text comment with no follow-up expected. ADO's <c>POST
/// /threads</c> endpoint returns the created thread (with its numeric ID and
/// the nested comment's ID), both of which are echoed back so callers can
/// link to the comment later if needed.</para>
///
/// <para>Snake-case via the global <see cref="PolyphonyJsonContext"/>
/// JsonSerializerOptions.</para>
/// </summary>
public sealed record PrPostCommentAdoResult
{
    /// <summary>PR number the comment was posted to (echoed for traceability).</summary>
    public required int PrNumber { get; init; }

    /// <summary>The Markdown comment body that was posted (echoed for traceability).</summary>
    public required string Body { get; init; }

    /// <summary>True when ADO accepted the thread (HTTP 200/201). False on any error path.</summary>
    public bool Posted { get; init; }

    /// <summary>
    /// Thread ID returned by ADO's <c>POST /threads</c> response. Null on
    /// any error path.
    /// </summary>
    public int? ThreadId { get; init; }

    /// <summary>
    /// Comment ID of the (single) top-level comment inside the created
    /// thread, returned by ADO. Null on any error path.
    /// </summary>
    public int? CommentId { get; init; }

    /// <summary>org/project/repo slug; empty when arguments failed validation.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>
    /// Canonical PR URL (<c>https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}</c>).
    /// Empty when arguments failed validation.
    /// </summary>
    public required string PrUrl { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Stable machine-readable error code. Vocabulary:
    /// <list type="bullet">
    ///   <item><c>invalid_argument</c> — required arg missing, non-positive PR number, or empty body.</item>
    ///   <item><c>pr_not_found</c> — ADO returned 404 (PR or repo not found).</item>
    ///   <item><c>ado_timeout</c> — every retry attempt timed out.</item>
    ///   <item><c>ado_failed</c> — non-success HTTP response (5xx or other) or unexpected error.</item>
    ///   <item><c>no_pat</c> — no PAT configured, or PAT rejected (401/403).</item>
    /// </list>
    /// Omitted on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}
