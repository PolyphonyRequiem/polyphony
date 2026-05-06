using System.Text.Json;
using ConsoleAppFramework;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree add</c> — create a new linked worktree at
/// <c>--path</c> with a freshly created branch <c>--branch</c> rooted
/// at <c>--ref</c> (defaults to <c>HEAD</c>).
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Create a new linked worktree.
    /// </summary>
    /// <param name="branch">Name of the new branch to create (passed to <c>git worktree add -b</c>).</param>
    /// <param name="path">Filesystem path the worktree will be created at.</param>
    /// <param name="gitRef">-- ref. Optional git ref the new branch is rooted at; defaults to <c>HEAD</c> when omitted.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("add")]
    public async Task<int> Add(
        string branch,
        string path,
        string? gitRef = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branch))
            {
                EmitAdd(branch, path, gitRef, "branch is required");
                return ExitCodes.Success;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                EmitAdd(branch, path, gitRef, "path is required");
                return ExitCodes.Success;
            }

            var result = await _git.WorktreeAddAsync(branch, path, gitRef, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // Prefer stderr — git puts the diagnostic there. Fall back
                // to stdout when stderr is empty (rare but possible).
                var err = !string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stderr.Trim()
                    : result.Stdout.Trim();
                EmitAdd(branch, path, gitRef, string.IsNullOrEmpty(err)
                    ? $"git worktree add exited with code {result.ExitCode}"
                    : err);
                return ExitCodes.Success;
            }

            EmitAdd(branch, path, gitRef, error: null);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Truly unexpected exception (e.g. process spawn failure).
            EmitAdd(branch, path, gitRef, ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitAdd(string branch, string path, string? gitRef, string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeAddResult
            {
                Branch = branch ?? string.Empty,
                Path = path ?? string.Empty,
                GitRef = string.IsNullOrEmpty(gitRef) ? null : gitRef,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeAddResult));
    }
}
