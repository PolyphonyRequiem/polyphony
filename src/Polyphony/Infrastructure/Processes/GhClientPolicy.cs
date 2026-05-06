namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Timeout + retry policy for <see cref="GhClient"/>.
///
/// Why this exists: <c>gh</c> has been observed to hang indefinitely on
/// network/credential operations (e.g., a 35+ minute hang on
/// <c>gh pr create</c> with no PR ever created on the GitHub side). The
/// policy bounds each attempt and bounds the total attempts.
///
/// Retry semantics (enforced by <see cref="GhClient"/>):
/// <list type="bullet">
///   <item>Per-attempt timeout: cancellation-token-driven; the runner kills
///     the entire <c>gh</c> process tree on timeout.</item>
///   <item>Retry only on timeout. Non-zero exit codes are real errors
///     (auth failure, branch missing, validation) and are not retried.</item>
///   <item>Caller-driven cancellation propagates immediately and is not
///     converted into a retryable timeout.</item>
///   <item>Backoff between retries is exponential (no jitter — single-process
///     CLI, not a high-concurrency service): <c>InitialBackoff * 2^(attempt-1)</c>.</item>
/// </list>
///
/// Mutation operations (e.g. <c>pr create</c>) layer reconciliation on top
/// of this policy — see <see cref="GhClient.CreatePullRequestAsync"/>.
/// </summary>
public sealed record GhClientPolicy
{
    /// <summary>Maximum attempts including the first. Must be &gt;= 1.</summary>
    public int MaxAttempts { get; }

    /// <summary>Hard timeout per attempt. Must be &gt; <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan PerAttemptTimeout { get; }

    /// <summary>Initial backoff between retries; doubles per subsequent retry. Must be &gt;= <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan InitialBackoff { get; }

    public GhClientPolicy(
        int maxAttempts,
        TimeSpan perAttemptTimeout,
        TimeSpan initialBackoff)
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

        MaxAttempts = maxAttempts;
        PerAttemptTimeout = perAttemptTimeout;
        InitialBackoff = initialBackoff;
    }

    /// <summary>
    /// Default policy: 3 attempts, 60s per attempt, 1s initial backoff
    /// (so retries happen at 1s, 2s after the first/second timeout).
    /// 60s is generous enough for <c>gh pr create</c> with a multi-KB body
    /// while still bounding the worst case to ~3 minutes total.
    /// </summary>
    public static GhClientPolicy Default { get; } = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromSeconds(60),
        initialBackoff: TimeSpan.FromSeconds(1));

    /// <summary>
    /// Single-attempt policy with no retry (useful in tests and
    /// for callers that want fail-fast semantics).
    /// </summary>
    public static GhClientPolicy NoRetry { get; } = new(
        maxAttempts: 1,
        perAttemptTimeout: TimeSpan.FromSeconds(60),
        initialBackoff: TimeSpan.Zero);
}
