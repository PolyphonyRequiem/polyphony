namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Low-level abstraction over <see cref="System.Diagnostics.Process"/> for
/// invoking external CLIs (twig, git, gh, dotnet). Mockable for unit tests.
///
/// Typed clients (<see cref="IGitClient"/>, <see cref="IGhClient"/>,
/// <see cref="ITwigClient"/>) wrap this interface and impose tool-specific
/// argument shapes and result parsing.
///
/// Commands SHOULD prefer the typed clients. Use <see cref="IProcessRunner"/>
/// directly only for one-off probes (e.g. <c>dotnet --version</c>) where
/// authoring a typed client would be overkill.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Spawn <paramref name="executable"/> with the supplied arguments,
    /// wait for it to exit, and return the captured exit code, stdout,
    /// and stderr as a <see cref="ProcessResult"/>.
    ///
    /// Stdout and stderr are captured separately. Buffering happens in
    /// memory — not suitable for streaming output.
    ///
    /// Cancelling the token kills the entire process tree and still
    /// drains any output captured before termination.
    /// </summary>
    /// <param name="executable">Name (resolved via PATH) or full path of the executable.</param>
    /// <param name="arguments">Argument list, passed via <c>StartInfo.ArgumentList</c> so each entry is escaped independently.</param>
    /// <param name="ct">Cancellation token. Triggers process-tree kill on cancellation.</param>
    /// <param name="workingDirectory">Optional working directory. When null, inherits the parent process CWD.</param>
    /// <param name="stdin">
    /// Optional content to write to the child's standard input. When non-null,
    /// stdin is redirected, the supplied string is written verbatim (UTF-8,
    /// no extra newline), and the stream is closed so the child sees EOF.
    /// When null, stdin is not redirected and the child inherits the parent's
    /// stdin handle (existing behaviour). Used by callers that need to feed
    /// large bodies via <c>--body-file -</c> to avoid command-line length limits.
    /// </param>
    /// <param name="environment">
    /// Optional additional environment variables to set on the child process,
    /// applied <em>on top of</em> the inherited parent environment. A
    /// <c>null</c> value removes the inherited variable for the child;
    /// a non-null value overrides it. Used by typed clients (<see cref="GhClient"/>)
    /// to opt subprocesses into specific behaviour (e.g. <c>GH_PROMPT_DISABLED=1</c>
    /// to surface hidden-prompt hangs as immediate errors instead of indefinite waits).
    /// When null/empty, only the parent process environment is inherited
    /// (existing behaviour).
    /// </param>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string?>? environment = null);
}
