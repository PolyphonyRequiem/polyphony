namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Result of a child-process invocation. Captures exit code and the full
/// contents of stdout and stderr. Buffers everything in memory — fine for
/// the small JSON / short-line outputs we shell out for. Not suitable for
/// streaming gigabytes.
/// </summary>
/// <param name="ExitCode">Process exit code. 0 conventionally means success.</param>
/// <param name="Stdout">Captured standard output as a single string.</param>
/// <param name="Stderr">Captured standard error as a single string.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>True when the process exited with code 0.</summary>
    public bool Succeeded => ExitCode == 0;
}
