using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree remove</c> — remove a linked worktree at
/// <c>--path</c>. Pass <c>--force</c> to allow removing dirty worktrees.
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Remove a linked worktree.
    /// </summary>
    /// <param name="path">Filesystem path of the worktree to remove.</param>
    /// <param name="force">Pass <c>--force</c> to git, allowing removal of a dirty worktree.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("remove")]
    [VerbResult(typeof(WorktreeRemoveResult))]
    public async Task<int> Remove(
        string path = "",
        bool force = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("worktree remove",
            ("--path", string.IsNullOrEmpty(path))) is { } halt)
            return halt;

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                EmitRemove(path, force, "path is required");
                return ExitCodes.Success;
            }

            var result = await _git.WorktreeRemoveAsync(path, force, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var err = !string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stderr.Trim()
                    : result.Stdout.Trim();
                EmitRemove(path, force, string.IsNullOrEmpty(err)
                    ? $"git worktree remove exited with code {result.ExitCode}"
                    : err);
                return ExitCodes.Success;
            }

            EmitRemove(path, force, error: null);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitRemove(path, force, ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitRemove(string path, bool force, string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeRemoveResult
            {
                Path = path ?? string.Empty,
                Force = force,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeRemoveResult));
    }
}
