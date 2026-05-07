namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Typed wrapper over the <c>gh</c> (GitHub) CLI. Consolidates the
/// authentication probe and pull-request operations the workflow scripts
/// rely on.
///
/// <para>
/// Failure semantics differ per method, but every method enforces a
/// timeout + retry policy (see <see cref="GhClientPolicy"/>) so a hanging
/// <c>gh</c> process cannot stall the orchestrator indefinitely.
/// </para>
///
/// <list type="bullet">
///   <item><see cref="GetAuthStatusAsync"/>: best-effort probe. On timeout,
///     returns an unauthenticated status with a "timed out" detail rather
///     than throwing — preflight should be able to report and remediate.</item>
///   <item><see cref="ListPullRequestsAsync"/>: returns empty on benign
///     non-zero exits (auth missing, repo unset, malformed JSON). <b>Throws
///     <see cref="ExternalToolTimeoutException"/> on timeout</b> so create-flow
///     gates do not silently confuse "I have no idea" with "no PR exists".</item>
///   <item><see cref="CreatePullRequestAsync"/>: throws
///     <see cref="ExternalToolException"/> for real errors (validation, branch
///     missing). On timeout, reconciles against an existing open PR for the
///     same (head, base) before retrying — a timed-out attempt may have been
///     accepted server-side. If reconciliation finds the PR, returns its URL;
///     otherwise retries until the policy is exhausted, then throws
///     <see cref="ExternalToolTimeoutException"/>.</item>
/// </list>
/// </summary>
public interface IGhClient
{
    /// <summary>
    /// <c>gh auth status</c>. gh emits its happy-path message to stderr,
    /// so the result type carries both a bool and the raw detail. On
    /// timeout, returns <c>IsAuthenticated = false</c> with a "timed out"
    /// detail (does not throw).
    /// </summary>
    Task<GhAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr list --repo {repoSlug} --json number,headRefName,url,mergedAt [filters]</c>.
    /// Returns empty when the call returns a non-zero exit (matches the
    /// existing <c>Invoke-GH</c> contract). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt
    /// exceeded the per-attempt timeout — callers that want "treat hang
    /// as no PRs" must explicitly catch this.
    /// </summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        string repoSlug,
        PrListFilters filters,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr create --repo {repoSlug} --base {base} --head {head} --title {title} --body {body}</c>.
    /// Returns the created PR's URL on success. Reconciles against an
    /// existing open PR for the same (head, base) on timeout and on
    /// "already exists" stderr — returns that PR's URL when a server-side
    /// duplicate is found. Throws <see cref="ExternalToolException"/> for
    /// real errors and <see cref="ExternalToolTimeoutException"/> when
    /// every attempt timed out without reconciliation finding the PR.
    /// </summary>
    Task<string> CreatePullRequestAsync(
        string repoSlug,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr merge {prNumber} --repo {repoSlug} --{method} [--admin] [--delete-branch] [--match-head-commit {sha}]</c>.
    /// On success, the merge SHA is populated via a follow-up
    /// <see cref="GetPullRequestStateAsync"/> call (gh's merge stdout is
    /// human-oriented and unreliable). On timeout, reconciles against the
    /// PR state — if the server already records the PR as merged, returns
    /// success with <see cref="GhMergeResult.AlreadyMerged"/> set true.
    /// Throws <see cref="ExternalToolException"/> for real errors and
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out without reconciliation confirming a merged state.
    /// </summary>
    /// <param name="repoSlug">Owner/repo slug, e.g. <c>polyphonyrequiem/polyphony</c>.</param>
    /// <param name="prNumber">PR number to merge.</param>
    /// <param name="method">
    /// Merge method. Use <see cref="GhMergeMethod.Merge"/> for merge-group
    /// PRs (required by ADR docs/decisions/branch-model.md). Squash and
    /// rebase are valid for impl PRs.
    /// </param>
    /// <param name="admin">Pass <c>--admin</c> to bypass branch-protection requirements.</param>
    /// <param name="deleteBranch">Pass <c>--delete-branch</c> to delete the head branch after merging.</param>
    /// <param name="matchHeadCommit">When set, pass <c>--match-head-commit {sha}</c> so gh refuses to merge if the head moved.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GhMergeResult> MergePullRequestAsync(
        string repoSlug,
        int prNumber,
        GhMergeMethod method,
        bool admin = false,
        bool deleteBranch = false,
        string? matchHeadCommit = null,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr view {prNumber} --repo {repoSlug} --json number,state,mergeCommit,headRefName,headRefOid</c>.
    /// Returns null when the PR cannot be found (non-zero exit). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out — used both for diagnostic introspection and for merge-call
    /// reconciliation (see <see cref="MergePullRequestAsync"/>).
    /// </summary>
    Task<GhPullRequestState?> GetPullRequestStateAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr view {prNumber} --repo {repoSlug} --json number,state,reviewDecision,reviews,headRefOid,baseRefName,headRefName,mergeable,mergedAt,mergeCommit,body</c>.
    /// Returns the rich snapshot consumed by <c>polyphony pr poll-status</c>:
    /// PR state, review decision, individual reviews, mergeability, head/base
    /// refs, and the body (so the caller can parse plan-PR front-matter).
    /// Returns null when the PR cannot be found (non-zero exit). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out — callers that want to treat a hang as "unknown" must catch.
    /// </summary>
    /// <summary>
    /// <c>gh pr view {prNumber} --repo {repoSlug} --json number,state,reviewDecision,reviews,headRefOid,baseRefName,headRefName,mergeable,mergedAt,mergeCommit,body</c>.
    /// Returns the rich snapshot consumed by <c>polyphony pr poll-status</c>:
    /// PR state, review decision, individual reviews, mergeability, head/base
    /// refs, and the body (so the caller can parse plan-PR front-matter).
    /// Returns null when the PR cannot be found (non-zero exit). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out — callers that want to treat a hang as "unknown" must catch.
    /// </summary>
    Task<GhPullRequestPollData?> GetPullRequestPollDataAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr view {prNumber} --repo {repoSlug} --json commits,body</c>
    /// — read just the fields the Phase 6 evidence floor needs (commit
    /// count + raw body for trim-and-measure). Returns a discriminated
    /// outcome so the caller can distinguish "PR genuinely missing"
    /// (<c>404</c> / "could not resolve") from "gh failed for some
    /// other reason" — the verb maps the two to distinct error codes
    /// (<c>pr_not_found</c> vs <c>gh_failed</c>) in its routing
    /// envelope. Subject to the standard retry-on-timeout policy
    /// (<see cref="GhClientPolicy"/>); when every attempt timed out the
    /// outcome is <see cref="GhEvidenceFloorOutcome.GhFailed"/> with a
    /// "timed out" detail rather than a thrown exception, because the
    /// caller is a routing-style verb that already reports timeouts as
    /// a routable outcome.
    /// </summary>
    Task<GhEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr view {prNumber} --repo {repoSlug} --json files</c>.
    /// Returns the list of files changed by the PR. Used by
    /// <c>polyphony pr validate-plan-diff</c> and the merge-time guard in
    /// <c>polyphony pr merge-plan-pr</c> to classify whether a child plan PR
    /// touched parent / ancestor / polyphony-state files.
    /// Returns null when the PR cannot be found (non-zero exit). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out — same contract as <see cref="GetPullRequestPollDataAsync"/>.
    /// </summary>
    Task<IReadOnlyList<GhPullRequestChangedFile>?> GetPullRequestFilesAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr diff {prNumber} --repo {repoSlug}</c> — return the unified
    /// diff text. Used by <c>polyphony plan extract-parent-patch</c> to
    /// extract just the hunks touching a parent's plan file.
    /// Returns null when the PR cannot be found (non-zero exit). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt timed
    /// out — same contract as <see cref="GetPullRequestPollDataAsync"/>.
    /// </summary>
    Task<string?> GetPullRequestDiffAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr edit {prNumber} --repo {repoSlug} --body-file -</c> — replace
    /// the body of an existing pull request. The body is piped via stdin
    /// (<c>--body-file -</c>) to avoid hitting platform argument-length limits
    /// on large plan-PR bodies. Subject to the same retry-on-timeout policy
    /// as every other <c>gh</c> call (<see cref="GhClientPolicy"/>).
    ///
    /// <para>Used by the P9 cascade remedy
    /// (<c>polyphony plan rebase-stale-descendant</c>) to rewrite the
    /// <c>ancestor_plan_generations</c> front-matter snapshot after a
    /// successful auto-rebase. Body-edit failure on that path is a
    /// recoverable, routable outcome — not a fatal exception — so this
    /// method <b>returns false on any failure</b> (non-zero exit, timeout,
    /// caller cancellation aside) and never throws
    /// <see cref="ExternalToolException"/> /
    /// <see cref="ExternalToolTimeoutException"/>. Caller-driven cancellation
    /// still propagates as <see cref="OperationCanceledException"/>.</para>
    /// </summary>
    /// <param name="repoSlug">Owner/repo slug, e.g. <c>polyphonyrequiem/polyphony</c>.</param>
    /// <param name="prNumber">PR number whose body will be replaced.</param>
    /// <param name="body">New body content. Written verbatim to gh's stdin (UTF-8, no extra newline).</param>
    /// <param name="ct">Cancellation token. Caller cancellation propagates immediately.</param>
    /// <returns>True on a clean success (gh exit 0). False on any non-success outcome.</returns>
    Task<bool> EditPullRequestBodyAsync(
        string repoSlug,
        int prNumber,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr comment {prNumber} --repo {repoSlug} --body-file -</c> — post a
    /// comment to a pull request. Body via stdin (same rationale as
    /// <see cref="EditPullRequestBodyAsync"/>). Subject to the standard
    /// retry-on-timeout policy.
    ///
    /// <para>Used for non-fatal cascade-remedy annotations (e.g. "🔄
    /// Auto-rebased onto <c>{parent}</c> after ancestor plan_generation
    /// bumped"). Comment-post failure must not poison the rebased outcome,
    /// so this method <b>returns false on any failure</b> (non-zero exit,
    /// timeout) and never throws <see cref="ExternalToolException"/> /
    /// <see cref="ExternalToolTimeoutException"/>. Caller-driven cancellation
    /// still propagates as <see cref="OperationCanceledException"/>.</para>
    /// </summary>
    /// <param name="repoSlug">Owner/repo slug, e.g. <c>polyphonyrequiem/polyphony</c>.</param>
    /// <param name="prNumber">PR number to comment on.</param>
    /// <param name="body">Comment body. Written verbatim to gh's stdin (UTF-8, no extra newline).</param>
    /// <param name="ct">Cancellation token. Caller cancellation propagates immediately.</param>
    /// <returns>True on a clean success (gh exit 0). False on any non-success outcome.</returns>
    Task<bool> CommentPullRequestAsync(
        string repoSlug,
        int prNumber,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr close {prNumber} --repo {repoSlug} [--comment "{comment}"]</c> — close
    /// an open pull request and optionally drop a closing comment in the same call.
    /// Subject to the standard retry-on-timeout policy.
    ///
    /// <para>Used by the P9 cascade-remedy <c>recreate</c> path
    /// (<c>polyphony plan recreate-stale-descendant</c>) to close a stale plan PR
    /// before a fresh PR is opened from the new ancestor tip. Close failure is a
    /// recoverable, routable outcome — not a fatal exception — so this method
    /// <b>returns false on any failure</b> (non-zero exit, timeout) and never throws
    /// <see cref="ExternalToolException"/> / <see cref="ExternalToolTimeoutException"/>.
    /// Caller-driven cancellation still propagates as
    /// <see cref="OperationCanceledException"/>.</para>
    ///
    /// <para>Idempotent: closing a PR that is already CLOSED returns true.
    /// gh prints a benign "Pull request #N is already closed" notice and exits 0
    /// in that case, so no special-casing is required by callers.</para>
    /// </summary>
    /// <param name="repoSlug">Owner/repo slug, e.g. <c>polyphonyrequiem/polyphony</c>.</param>
    /// <param name="prNumber">PR number to close.</param>
    /// <param name="commentBeforeClose">
    /// Optional closing comment; passed via <c>--comment "{value}"</c>. Empty / null
    /// omits the flag (close-only). The string is forwarded verbatim — quoting is
    /// handled by the process runner.
    /// </param>
    /// <param name="ct">Cancellation token. Caller cancellation propagates immediately.</param>
    /// <returns>True on a clean success (gh exit 0). False on any non-success outcome.</returns>
    Task<bool> ClosePullRequestAsync(
        string repoSlug,
        int prNumber,
        string commentBeforeClose,
        CancellationToken ct = default);
}
