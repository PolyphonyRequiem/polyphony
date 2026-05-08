namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Thrown by <see cref="ProcessRunner"/> when the supplied
/// <see cref="CancellationToken"/> fired (caller cancellation OR a linked
/// per-attempt timeout from a typed client like <see cref="GhClient"/>).
/// Carries whatever stdout/stderr the runner had already drained from
/// the child's pipes before the kill, plus the wall-clock elapsed time
/// since the process started.
///
/// Subclasses <see cref="OperationCanceledException"/> so existing
/// <c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c>
/// callers (notably <see cref="GhClient.RunWithRetryAsync"/>) still
/// match it correctly and can decide whether to propagate or treat as
/// a per-attempt timeout. Typed clients that want the buffered output
/// can catch <see cref="ProcessCanceledException"/> ahead of the bare
/// <see cref="OperationCanceledException"/> arm.
///
/// "Canceled" not "Timeout" because <see cref="ProcessRunner"/> sees only
/// "the token fired" — it cannot distinguish caller cancellation from a
/// timeout fired by an outer linked CTS. The caller (e.g. <see cref="GhClient"/>)
/// has the timeout context and reinterprets accordingly.
/// </summary>
public sealed class ProcessCanceledException : OperationCanceledException
{
    /// <summary>The executable that was invoked.</summary>
    public string Executable { get; }

    /// <summary>The argument list passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Stdout captured before the process tree was killed. May be empty.</summary>
    public string BufferedStdout { get; }

    /// <summary>Stderr captured before the process tree was killed. May be empty.</summary>
    public string BufferedStderr { get; }

    /// <summary>Wall-clock time between <see cref="System.Diagnostics.Process.Start()"/> and the kill.</summary>
    public TimeSpan Elapsed { get; }

    public ProcessCanceledException(
        string executable,
        IReadOnlyList<string> arguments,
        string bufferedStdout,
        string bufferedStderr,
        TimeSpan elapsed)
        : base(BuildMessage(executable, arguments, elapsed))
    {
        Executable = executable;
        Arguments = arguments;
        BufferedStdout = bufferedStdout ?? string.Empty;
        BufferedStderr = bufferedStderr ?? string.Empty;
        Elapsed = elapsed;
    }

    private static string BuildMessage(string exe, IReadOnlyList<string> args, TimeSpan elapsed)
    {
        var argLine = string.Join(' ', args);
        return $"{exe} {argLine} canceled after {elapsed.TotalSeconds:0.#}s.";
    }
}
