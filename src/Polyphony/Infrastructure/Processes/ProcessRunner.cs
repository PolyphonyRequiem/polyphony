using System.Diagnostics;
using System.Text;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.
///
/// Implementation notes:
/// - Reads stdout and stderr in parallel, BEFORE awaiting WaitForExitAsync.
///   The classic deadlock here is "wait, then read": if the child writes
///   more than the OS pipe buffer it blocks waiting for someone to drain,
///   and the parent blocks on WaitForExit. Both sides hang.
/// - On cancellation (caller-requested OR per-attempt timeout fired by an
///   outer linked CTS), kills the entire process tree, drains whatever
///   is buffered, and throws <see cref="ProcessCanceledException"/>
///   carrying the captured stdout/stderr + elapsed time. Cancellation
///   coverage spans the entire post-Start lifetime — including the stdin
///   write — so a cancel during a slow stdin write also kills cleanly.
/// - Stateless. Safe to register as a singleton.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool closeStdin = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null || closeStdin,
            // #116: force UTF-8 on all three redirected streams. The .NET
            // default is Console.InputEncoding / Console.OutputEncoding,
            // which on Windows defaults to the system code page (cp1252
            // on most installs). When we pipe a Unicode comment body
            // (`→`, `🔄`, …) into `gh pr comment --body-file -` the
            // bytes hit gh as cp1252 and the unmappable characters fall
            // back to `?` (0x3F) or SUB (0x1A), producing the `??` /
            // `^Z` mojibake observed on PR #113. UTF-8 round-trips ASCII
            // unchanged and carries Unicode through losslessly.
            StandardInputEncoding = (stdin is not null || closeStdin) ? Encoding.UTF8 : null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Apply caller-supplied environment overrides on top of the inherited
        // parent environment. Null value removes the variable for the child,
        // matching ProcessStartInfo.Environment semantics.
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stopwatch = Stopwatch.StartNew();

        // Issue #209 (Windows gh hang via inherited console handle): when
        // closeStdin was requested without a stdin payload, immediately
        // close the (now-redirected) input so the child sees EOF on read
        // instead of inheriting whatever stale handle the parent had.
        if (stdin is null && closeStdin)
        {
            process.StandardInput.Close();
        }

        // Begin draining BOTH pipes immediately. If we waited for exit first
        // and the child wrote enough to fill the OS pipe buffer (~64KB), the
        // child would block on the write and we would block on WaitForExit.
        // CT=None: drain tasks must keep running on cancel so we can capture
        // whatever the child emitted before the kill.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            // Stdin write happens INSIDE the cancel-handling try/catch so
            // a slow stdin write that's cancelled also triggers the kill+drain
            // path (was previously a coverage gap — a cancel during stdin
            // write left the child running and threw a bare OCE).
            if (stdin is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled OR an outer linked CTS fired (per-attempt
            // timeout in GhClient). Kill the tree, drain whatever buffered,
            // then throw the typed exception so callers can surface
            // diagnostic context (last gh stderr with GH_DEBUG=api set,
            // for example) instead of a bare OperationCanceledException.
            TryKillTree(process);
            stopwatch.Stop();

            string capturedStdout = string.Empty;
            string capturedStderr = string.Empty;
            try { capturedStdout = await stdoutTask.ConfigureAwait(false); }
            catch { /* drain failure shouldn't mask the cancellation. */ }
            try { capturedStderr = await stderrTask.ConfigureAwait(false); }
            catch { /* see above. */ }

            throw new ProcessCanceledException(
                executable, arguments, capturedStdout, capturedStderr, stopwatch.Elapsed);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited between the HasExited check and Kill; ignore.
        }
    }
}
