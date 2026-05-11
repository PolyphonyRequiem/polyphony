using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree status</c> — report cleanliness and current branch
/// of a single worktree at <c>--path</c> (defaults to the current directory).
///
/// <para>Routing-style verb: always exits 0; consumers branch on
/// <c>output.is_clean</c> and <c>output.error</c>. Emits a
/// <see cref="WorktreeStatusResult"/> envelope on stdout.</para>
///
/// <para>The verb is the read-only sibling of <c>worktree assert-clean</c>:
/// <see cref="Status"/> reports facts; <c>assert-clean</c> turns those facts
/// into a routing decision.</para>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Inspect a worktree's cleanliness.
    /// </summary>
    /// <param name="path">Worktree path to inspect. Defaults to the current directory when omitted.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    [VerbResult(typeof(WorktreeStatusResult))]
    public async Task<int> Status(
        string path = "",
        CancellationToken ct = default)
    {
        var resolvedPath = string.IsNullOrEmpty(path)
            ? Environment.CurrentDirectory
            : path;

        try
        {
            resolvedPath = System.IO.Path.GetFullPath(resolvedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException or PathTooLongException)
        {
            EmitStatus(resolvedPath, isClean: false, currentBranch: null, dirtyPaths: [], error: ex.Message);
            return ExitCodes.Success;
        }

        if (!Directory.Exists(resolvedPath))
        {
            EmitStatus(resolvedPath, isClean: false, currentBranch: null, dirtyPaths: [],
                error: $"path does not exist or is not a directory: {resolvedPath}");
            return ExitCodes.Success;
        }

        try
        {
            var dirty = await _git.GetStatusAsync(resolvedPath, ct).ConfigureAwait(false);
            var branch = await _git.GetCurrentBranchAsync(resolvedPath, ct).ConfigureAwait(false);
            EmitStatus(
                resolvedPath,
                isClean: dirty.Count == 0,
                currentBranch: string.IsNullOrEmpty(branch) ? null : branch,
                dirtyPaths: dirty,
                error: null);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            // git status failed — most often "not a git repository". Surface
            // stderr verbatim so workflows can route on the diagnostic.
            var detail = !string.IsNullOrWhiteSpace(ex.Stderr) ? ex.Stderr.Trim() : ex.Message;
            EmitStatus(resolvedPath, isClean: false, currentBranch: null, dirtyPaths: [], error: detail);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            // Routing-style verb: ALWAYS exit 0 once we've decided to emit
            // an envelope. Consumers branch on output.error / output.is_clean.
            EmitStatus(resolvedPath, isClean: false, currentBranch: null, dirtyPaths: [], error: ex.Message);
            return ExitCodes.Success;
        }
    }

    private static void EmitStatus(
        string path,
        bool isClean,
        string? currentBranch,
        IReadOnlyList<string> dirtyPaths,
        string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeStatusResult
            {
                Path = path,
                IsClean = isClean,
                CurrentBranch = currentBranch,
                DirtyPaths = dirtyPaths,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeStatusResult));
    }
}
