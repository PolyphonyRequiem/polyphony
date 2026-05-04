using System.Diagnostics;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.
///
/// Implementation notes:
/// - Reads stdout and stderr in parallel, BEFORE awaiting WaitForExitAsync.
///   The classic deadlock here is "wait, then read": if the child writes
///   more than the OS pipe buffer it blocks waiting for someone to drain,
///   and the parent blocks on WaitForExit. Both sides hang.
/// - On cancellation, kills the entire process tree, then still awaits the
///   read tasks so we capture any buffered output before returning.
/// - Stateless. Safe to register as a singleton.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Begin draining BOTH pipes immediately. If we waited for exit first
        // and the child wrote enough to fill the OS pipe buffer (~64KB), the
        // child would block on the write and we would block on WaitForExit.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled. Kill the tree, drain whatever buffered, then propagate.
            TryKillTree(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Ignore drain exceptions — the original cancellation is what matters.
            }
            throw;
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
