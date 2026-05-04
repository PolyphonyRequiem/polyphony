namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Thrown by typed clients (<see cref="IGitClient"/>, <see cref="IGhClient"/>,
/// <see cref="ITwigClient"/>) when an external tool exits non-zero in a way
/// the caller cannot recover from.
///
/// Carries the full invocation context so the workflow JSON output can
/// surface a meaningful diagnostic instead of a bare exit code.
/// </summary>
public sealed class ExternalToolException : Exception
{
    /// <summary>The executable that was invoked (e.g. <c>git</c>, <c>gh</c>).</summary>
    public string Executable { get; }

    /// <summary>The argument list passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The exit code the process returned.</summary>
    public int ExitCode { get; }

    /// <summary>Captured standard error contents.</summary>
    public string Stderr { get; }

    /// <summary>Captured standard output contents.</summary>
    public string Stdout { get; }

    public ExternalToolException(
        string executable,
        IReadOnlyList<string> arguments,
        int exitCode,
        string stdout,
        string stderr)
        : base(BuildMessage(executable, arguments, exitCode, stderr))
    {
        Executable = executable;
        Arguments = arguments;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }

    private static string BuildMessage(string exe, IReadOnlyList<string> args, int code, string stderr)
    {
        var argLine = string.Join(' ', args);
        var stderrSnippet = string.IsNullOrWhiteSpace(stderr)
            ? "(no stderr)"
            : stderr.Trim();
        return $"{exe} {argLine} exited {code}: {stderrSnippet}";
    }
}
