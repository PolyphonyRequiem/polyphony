namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Thrown by <see cref="GhClient"/> (and future timeout-aware clients) when
/// every attempt at invoking an external tool exceeded its per-attempt
/// timeout. Carries the invocation context plus the policy used so callers
/// (and JSON output paths) can surface a meaningful diagnostic.
///
/// Sibling — not a subclass — of <see cref="ExternalToolException"/>.
/// The two failure shapes are genuinely different: an
/// <see cref="ExternalToolException"/> carries an exit code and captured
/// stdout/stderr; a timeout has none of those. Callers that want to react
/// to either failure mode catch both explicitly.
/// </summary>
public sealed class ExternalToolTimeoutException : Exception
{
    /// <summary>The executable that was invoked (e.g. <c>gh</c>).</summary>
    public string Executable { get; }

    /// <summary>The argument list passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Number of attempts made before giving up (typically <c>policy.MaxAttempts</c>).</summary>
    public int Attempts { get; }

    /// <summary>The per-attempt timeout that was applied.</summary>
    public TimeSpan TimeoutPerAttempt { get; }

    public ExternalToolTimeoutException(
        string executable,
        IReadOnlyList<string> arguments,
        int attempts,
        TimeSpan timeoutPerAttempt)
        : base(BuildMessage(executable, arguments, attempts, timeoutPerAttempt))
    {
        Executable = executable;
        Arguments = arguments;
        Attempts = attempts;
        TimeoutPerAttempt = timeoutPerAttempt;
    }

    private static string BuildMessage(
        string exe,
        IReadOnlyList<string> args,
        int attempts,
        TimeSpan timeout)
    {
        var argLine = string.Join(' ', args);
        return $"{exe} {argLine} timed out after {attempts} attempt(s) of {timeout.TotalSeconds:0.#}s each.";
    }
}
