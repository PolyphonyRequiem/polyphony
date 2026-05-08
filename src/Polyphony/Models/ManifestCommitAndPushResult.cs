namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony manifest commit-and-push</c>.
///
/// <para>The verb encodes the manifest-lifecycle invariant from the
/// branch-model ADR (§Run manifest + concurrent-run lock): every run
/// commits <c>.polyphony/run.yaml</c> on <c>feature/{root_id}</c>. It
/// validates the worktree is on the right branch, that the file at
/// <c>--path</c> parses as a manifest whose <c>root_id</c> matches the
/// supplied <c>--root-id</c>, then stages, commits, and pushes the
/// manifest to <c>origin/feature/{root_id}</c>.</para>
///
/// <para>Idempotent contract: re-running the verb when the manifest at
/// HEAD already matches the on-disk file is a no-op
/// (<c>pushed: false</c>, <c>no_op_reason: "no_changes"</c>) and exits 0.
/// Routing-style: errors are surfaced in <c>error</c>/<c>error_code</c>
/// without crashing the workflow.</para>
/// </summary>
public sealed record ManifestCommitAndPushResult
{
    /// <summary>Branch the commit (if any) was pushed to.</summary>
    public required string Branch { get; init; }

    /// <summary>Manifest path that was committed.</summary>
    public required string Path { get; init; }

    /// <summary>True when a commit was created and pushed; false on no-op or error.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Apex root id this manifest belongs to.</summary>
    public int RootId { get; init; }

    /// <summary>HEAD SHA after the push completed. Null on no-op or error.</summary>
    public string? CommitSha { get; init; }

    /// <summary>
    /// Why no commit was produced. <c>"no_changes"</c> when staging the
    /// manifest left nothing for git to commit. Null when a commit was
    /// created.
    /// </summary>
    public string? NoOpReason { get; init; }

    /// <summary>
    /// Stable error classifier for routing. One of:
    /// <list type="bullet">
    ///   <item><c>invalid_inputs</c> — missing/invalid <c>--root-id</c>.</item>
    ///   <item><c>manifest_missing</c> — file at <c>--path</c> not present.</item>
    ///   <item><c>manifest_parse_failed</c> — file present but not a valid manifest.</item>
    ///   <item><c>manifest_root_mismatch</c> — manifest <c>root_id</c> != <c>--root-id</c>.</item>
    ///   <item><c>wrong_branch</c> — worktree on a branch other than <c>feature/{root_id}</c>.</item>
    ///   <item><c>git_failed</c> — stage/commit/push exited non-zero.</item>
    /// </list>
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error message; null on success or no-op.</summary>
    public string? Error { get; init; }
}
