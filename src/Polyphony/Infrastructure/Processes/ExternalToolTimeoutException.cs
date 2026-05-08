using System.Text;
using System.Text.RegularExpressions;

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
///
/// When the underlying <see cref="ProcessRunner"/> drained any output
/// before being killed, the LAST attempt's buffered stdout/stderr are
/// preserved on <see cref="LastBufferedStdout"/> / <see cref="LastBufferedStderr"/>.
/// The exception message includes a tail snippet of stderr (the most
/// diagnostically useful end of a verbose log like <c>GH_DEBUG=api</c>),
/// with obvious tokens redacted before display.
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

    /// <summary>Stdout drained from the LAST timed-out attempt (empty when nothing was emitted).</summary>
    public string LastBufferedStdout { get; }

    /// <summary>Stderr drained from the LAST timed-out attempt (empty when nothing was emitted).</summary>
    public string LastBufferedStderr { get; }

    /// <summary>Wall-clock elapsed time of the LAST timed-out attempt before the kill.</summary>
    public TimeSpan LastElapsed { get; }

    public ExternalToolTimeoutException(
        string executable,
        IReadOnlyList<string> arguments,
        int attempts,
        TimeSpan timeoutPerAttempt)
        : this(executable, arguments, attempts, timeoutPerAttempt,
               lastBufferedStdout: string.Empty,
               lastBufferedStderr: string.Empty,
               lastElapsed: TimeSpan.Zero)
    {
    }

    public ExternalToolTimeoutException(
        string executable,
        IReadOnlyList<string> arguments,
        int attempts,
        TimeSpan timeoutPerAttempt,
        string lastBufferedStdout,
        string lastBufferedStderr,
        TimeSpan lastElapsed)
        : base(BuildMessage(executable, arguments, attempts, timeoutPerAttempt, lastBufferedStderr))
    {
        Executable = executable;
        Arguments = arguments;
        Attempts = attempts;
        TimeoutPerAttempt = timeoutPerAttempt;
        LastBufferedStdout = lastBufferedStdout ?? string.Empty;
        LastBufferedStderr = lastBufferedStderr ?? string.Empty;
        LastElapsed = lastElapsed;
    }

    /// <summary>
    /// Maximum stderr tail length included in the formatted exception
    /// message. Generous enough to capture the last few HTTP exchanges
    /// from <c>GH_DEBUG=api</c> (typical ~200 bytes per request) while
    /// keeping the routing-envelope error string bounded.
    /// </summary>
    internal const int StderrTailLimitChars = 4096;

    /// <summary>
    /// Format a user-facing error message for a routing envelope
    /// <c>error_message</c> field, using a caller-supplied short tool
    /// description (e.g. <c>"gh pr view"</c>) instead of the full
    /// argument list. Includes attempt count, per-attempt timeout, the
    /// last attempt's wall-clock elapsed, and the redacted, tail-truncated
    /// last-attempt stderr (or a hint to set <c>GH_DEBUG=api</c> when no
    /// stderr was captured).
    ///
    /// Verbs that catch this exception should prefer this method over
    /// constructing their own ad-hoc terse string — the goal is for the
    /// operator gate prompt to carry actionable diagnostic context, not
    /// just "timed out after N attempts".
    /// </summary>
    public string FormatErrorMessage(string toolDescription)
    {
        var sb = new StringBuilder();
        sb.Append(toolDescription)
          .Append(" timed out after ").Append(Attempts)
          .Append(" attempt(s) of ").Append(TimeoutPerAttempt.TotalSeconds.ToString("0.#")).Append("s each");
        if (LastElapsed > TimeSpan.Zero)
        {
            sb.Append(" (last attempt ").Append(LastElapsed.TotalSeconds.ToString("0.#")).Append("s)");
        }
        sb.Append('.');

        if (string.IsNullOrWhiteSpace(LastBufferedStderr))
        {
            sb.Append(" No stderr was captured before kill — set GH_DEBUG=api on the parent shell to surface gh's HTTP trace next run.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Last attempt stderr (tail):");
            sb.Append(TakeTail(RedactTokens(LastBufferedStderr), StderrTailLimitChars));
        }
        return sb.ToString();
    }

    private static string BuildMessage(
        string exe,
        IReadOnlyList<string> args,
        int attempts,
        TimeSpan timeout,
        string lastStderr)
    {
        var argLine = string.Join(' ', args);
        var sb = new StringBuilder();
        sb.Append(exe).Append(' ').Append(argLine)
          .Append(" timed out after ").Append(attempts).Append(" attempt(s) of ")
          .Append(timeout.TotalSeconds.ToString("0.#")).Append("s each.");

        if (string.IsNullOrWhiteSpace(lastStderr))
        {
            sb.Append(" (no stderr emitted before kill — set GH_DEBUG=api on the parent shell to surface gh's API trace next run.)");
        }
        else
        {
            var redacted = RedactTokens(lastStderr);
            var tail = TakeTail(redacted, StderrTailLimitChars);
            sb.AppendLine();
            sb.AppendLine("Last attempt stderr (tail):");
            sb.Append(tail);
        }
        return sb.ToString();
    }

    private static string TakeTail(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        return "…" + s.Substring(s.Length - maxChars);
    }

    /// <summary>
    /// Best-effort redaction of obvious GitHub tokens / Authorization
    /// headers before the snippet is rendered into a user-facing error
    /// message. Not a security boundary — tokens should not be leaking
    /// through stderr in the first place — but a defensive line of
    /// defence when surfacing third-party output (e.g. <c>GH_DEBUG=api</c>
    /// HTTP traces) into the routing envelope.
    /// </summary>
    private static string RedactTokens(string s)
    {
        // ghp_xxx / gho_xxx / ghu_xxx / ghs_xxx / ghr_xxx (40-char hex tail)
        s = Regex.Replace(s, @"gh[opusr]_[A-Za-z0-9]{20,}", "[REDACTED-TOKEN]");
        // github_pat_xxx
        s = Regex.Replace(s, @"github_pat_[A-Za-z0-9_]{20,}", "[REDACTED-TOKEN]");
        // Authorization: Bearer/Basic/token <value>
        s = Regex.Replace(s, @"(?i)(Authorization:\s*(?:Bearer|Basic|token)\s+)\S+", "$1[REDACTED]");
        // X-Github-Token: <value>
        s = Regex.Replace(s, @"(?i)(X-Github-Token:\s*)\S+", "$1[REDACTED]");
        return s;
    }
}
