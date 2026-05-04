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
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null);
}
