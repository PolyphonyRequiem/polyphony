namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Timeout + retry policy for <see cref="AdoClient"/>. Mirrors
/// <see cref="Polyphony.Infrastructure.Processes.GhClientPolicy"/> so the two
/// platform clients have identical failure-shaping semantics.
///
/// <para>
/// Why this exists: dev.azure.com has been observed to wedge on flaky
/// network paths and on identity-service hiccups. The policy bounds each
/// attempt and bounds the total attempts so an HTTP call cannot stall the
/// orchestrator indefinitely.
/// </para>
///
/// Retry semantics (enforced by <see cref="AdoClient"/>):
/// <list type="bullet">
///   <item>Per-attempt timeout: cancellation-token-driven. The HTTP call is
///     cancelled when the per-attempt timeout fires.</item>
///   <item>Retry only on timeout. HTTP error status codes (4xx, 5xx) are
///     real signals — 401/403 in particular must not be retried because the
///     PAT will not change between attempts.</item>
///   <item>Caller-driven cancellation propagates immediately and is not
///     converted into a retryable timeout.</item>
///   <item>Backoff between retries is exponential (no jitter — single-process
///     CLI, not a high-concurrency service): <c>InitialBackoff * 2^(attempt-1)</c>.</item>
/// </list>
/// </summary>
public sealed record AdoClientPolicy
{
    /// <summary>Maximum attempts including the first. Must be &gt;= 1.</summary>
    public int MaxAttempts { get; }

    /// <summary>Hard timeout per attempt. Must be &gt; <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan PerAttemptTimeout { get; }

    /// <summary>Initial backoff between retries; doubles per subsequent retry. Must be &gt;= <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan InitialBackoff { get; }

    /// <summary>
    /// Number of GET polls to issue after a successful ADO complete-PR PATCH
    /// when the PATCH response itself does not carry the merge commit SHA.
    /// ADO's PR completion is asynchronous on the server side: the PATCH
    /// returns 200 OK as soon as the merge request is accepted, but the
    /// actual ref update runs on the CompletionQueueWorker and the
    /// <c>lastMergeCommit.commitId</c> field is not populated in the PATCH
    /// response body. A subsequent GET on the same PR typically returns the
    /// populated SHA within ~1s. Must be &gt;= 0; <c>0</c> disables polling
    /// (useful in tests). See
    /// <see cref="AdoClient.CompletePullRequestAsync"/>.
    /// </summary>
    public int CompletionMergePollAttempts { get; }

    /// <summary>
    /// Initial delay before the first post-PATCH merge-commit poll; doubles
    /// per subsequent poll. Must be &gt;= <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan CompletionMergePollInitialDelay { get; }

    public AdoClientPolicy(
        int maxAttempts,
        TimeSpan perAttemptTimeout,
        TimeSpan initialBackoff)
        : this(maxAttempts, perAttemptTimeout, initialBackoff,
               completionMergePollAttempts: DefaultCompletionMergePollAttempts,
               completionMergePollInitialDelay: DefaultCompletionMergePollInitialDelay)
    {
    }

    public AdoClientPolicy(
        int maxAttempts,
        TimeSpan perAttemptTimeout,
        TimeSpan initialBackoff,
        int completionMergePollAttempts,
        TimeSpan completionMergePollInitialDelay)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "MaxAttempts must be >= 1.");
        }
        if (perAttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perAttemptTimeout), perAttemptTimeout, "PerAttemptTimeout must be positive.");
        }
        if (initialBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialBackoff), initialBackoff, "InitialBackoff must be non-negative.");
        }
        if (completionMergePollAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionMergePollAttempts), completionMergePollAttempts,
                "CompletionMergePollAttempts must be non-negative.");
        }
        if (completionMergePollInitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionMergePollInitialDelay), completionMergePollInitialDelay,
                "CompletionMergePollInitialDelay must be non-negative.");
        }

        MaxAttempts = maxAttempts;
        PerAttemptTimeout = perAttemptTimeout;
        InitialBackoff = initialBackoff;
        CompletionMergePollAttempts = completionMergePollAttempts;
        CompletionMergePollInitialDelay = completionMergePollInitialDelay;
    }

    private const int DefaultCompletionMergePollAttempts = 8;
    private static readonly TimeSpan DefaultCompletionMergePollInitialDelay =
        TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Default policy: 3 attempts, 30s per attempt, 1s initial backoff
    /// (so retries happen at 1s, 2s after the first/second timeout).
    /// 30s is generous enough for the connection-data probe over a slow link
    /// while still bounding the worst case to ~90s total.
    /// <para>
    /// Completion merge-commit poll: 8 attempts at 250ms × 2^(n-1) =
    /// {0.25, 0.5, 1, 2, 4, 8, 16, 32}s — total ~64s worst case after a
    /// successful PATCH but bails on the first attempt that returns a SHA
    /// (typical real-world case is the first poll, ~250ms after the PATCH).
    /// </para>
    /// </summary>
    public static AdoClientPolicy Default { get; } = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromSeconds(30),
        initialBackoff: TimeSpan.FromSeconds(1),
        completionMergePollAttempts: DefaultCompletionMergePollAttempts,
        completionMergePollInitialDelay: DefaultCompletionMergePollInitialDelay);

    /// <summary>
    /// Single-attempt policy with no retry (useful in tests and for callers
    /// that want fail-fast semantics). The merge-commit post-PATCH poll is
    /// also disabled (0 attempts) so tests can pin behavior without mocking
    /// follow-up GETs.
    /// </summary>
    public static AdoClientPolicy NoRetry { get; } = new(
        maxAttempts: 1,
        perAttemptTimeout: TimeSpan.FromSeconds(30),
        initialBackoff: TimeSpan.Zero,
        completionMergePollAttempts: 0,
        completionMergePollInitialDelay: TimeSpan.Zero);
}
