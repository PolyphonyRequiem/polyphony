namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr vote-ado</c> — the ADO equivalent of
/// <c>gh pr review --approve|--request-changes|--comment</c>. Records the
/// vote that was applied (or attempted) and surfaces a stable
/// <see cref="ErrorCode"/> the workflow YAML can branch on.
///
/// <para>The verb always exits 0 (routing-style); failure modes surface in
/// the envelope. Successful invocations have <see cref="Submitted"/> = true,
/// <see cref="Error"/> = null, <see cref="ErrorCode"/> = null.</para>
///
/// <para>Vote vocabulary mirrors ADO's reviewer enum but is exposed as the
/// human-friendly names accepted on the command line:</para>
/// <list type="bullet">
///   <item><c>approve</c> — vote 10.</item>
///   <item><c>approve-with-suggestions</c> — vote 5.</item>
///   <item><c>reset</c> — vote 0 (clear any prior vote).</item>
///   <item><c>wait-for-author</c> — vote -5.</item>
///   <item><c>reject</c> — vote -10.</item>
/// </list>
///
/// <para>Snake-case via the global <see cref="PolyphonyJsonContext"/>
/// JsonSerializerOptions.</para>
/// </summary>
public sealed record PrVoteAdoResult
{
    /// <summary>PR number the vote was applied to (echoed for traceability).</summary>
    public required int PrNumber { get; init; }

    /// <summary>Reviewer identity GUID the vote was submitted on behalf of.</summary>
    public required string ReviewerId { get; init; }

    /// <summary>The requested vote name, exactly as supplied on the CLI.</summary>
    public required string Vote { get; init; }

    /// <summary>Numeric vote value sent to ADO (0 when the requested name failed validation).</summary>
    public int VoteValue { get; init; }

    /// <summary>True when ADO accepted the vote (HTTP 200). False on any error path.</summary>
    public bool Submitted { get; init; }

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
    ///   <item><c>invalid_argument</c> — required arg missing or non-positive PR number.</item>
    ///   <item><c>invalid_vote</c> — <c>--vote</c> name not in the accepted set.</item>
    ///   <item><c>pr_not_found</c> — ADO returned 404 (PR or reviewer not found).</item>
    ///   <item><c>ado_timeout</c> — every retry attempt timed out.</item>
    ///   <item><c>ado_failed</c> — non-success HTTP response (5xx or other) or unexpected error.</item>
    ///   <item><c>no_pat</c> — no PAT configured, or PAT rejected (401/403).</item>
    /// </list>
    /// Omitted on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}
