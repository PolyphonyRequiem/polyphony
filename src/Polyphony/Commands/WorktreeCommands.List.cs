using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree list</c> — enumerate linked worktrees by
/// invoking <c>git worktree list --porcelain</c> and parsing the
/// canonical block format into structured <see cref="WorktreeEntry"/>
/// records.
///
/// <para>Porcelain format: each worktree is a block of <c>key value</c>
/// lines terminated by a blank line. Recognised keys are
/// <c>worktree</c> (path), <c>HEAD</c> (sha), <c>branch refs/heads/{name}</c>,
/// <c>bare</c>, and <c>detached</c>. Unknown keys are ignored.</para>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// List all linked worktrees of the current repository.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [Command("list")]
    [VerbResult(typeof(WorktreeListResult))]
    public async Task<int> List(CancellationToken ct = default)
    {
        try
        {
            var result = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var err = !string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stderr.Trim()
                    : result.Stdout.Trim();
                EmitList([], string.IsNullOrEmpty(err)
                    ? $"git worktree list exited with code {result.ExitCode}"
                    : err);
                return ExitCodes.Success;
            }

            try
            {
                var entries = ParsePorcelain(result.Stdout);
                EmitList(entries, error: null);
                return ExitCodes.Success;
            }
            catch (FormatException pex)
            {
                EmitList([], pex.Message);
                return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitList([], ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    /// <summary>
    /// Parse <c>git worktree list --porcelain</c> output into a list of
    /// <see cref="WorktreeEntry"/> records. Empty input yields an empty
    /// list. Blocks without a leading <c>worktree</c> line are rejected
    /// as malformed (raises <see cref="FormatException"/>).
    /// </summary>
    /// <remarks>
    /// Visible for testing — keeps the parser exercisable without
    /// shelling out to a real git binary.
    /// </remarks>
    internal static IReadOnlyList<WorktreeEntry> ParsePorcelain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var entries = new List<WorktreeEntry>();
        // Normalise CRLF so split-on-LF works on Windows git output.
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        string? path = null;
        string? head = null;
        string? branch = null;
        bool isBare = false;
        bool isDetached = false;
        bool inBlock = false;

        void Flush()
        {
            if (!inBlock) return;
            if (path is null)
            {
                throw new FormatException("git worktree list --porcelain block missing 'worktree' line");
            }
            entries.Add(new WorktreeEntry
            {
                Path = path,
                Branch = branch,
                Head = head,
                IsBare = isBare,
                IsDetached = isDetached,
            });
            path = null;
            head = null;
            branch = null;
            isBare = false;
            isDetached = false;
            inBlock = false;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                Flush();
                continue;
            }

            inBlock = true;
            // Each meaningful line is "<key>" or "<key> <value>" — split
            // at the first space only so values containing spaces stay intact.
            var spaceIdx = line.IndexOf(' ');
            var key = spaceIdx < 0 ? line : line[..spaceIdx];
            var value = spaceIdx < 0 ? string.Empty : line[(spaceIdx + 1)..];

            switch (key)
            {
                case "worktree":
                    path = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    // Strip the canonical "refs/heads/" prefix; keep the raw
                    // value when the prefix is absent (defensive).
                    branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? value["refs/heads/".Length..]
                        : value;
                    break;
                case "bare":
                    isBare = true;
                    break;
                case "detached":
                    isDetached = true;
                    break;
                default:
                    // Strict parse: unknown keys are ignored, not failed.
                    break;
            }
        }

        // Flush trailing block when input doesn't end with a blank line.
        Flush();
        return entries;
    }

    private static void EmitList(IReadOnlyList<WorktreeEntry> entries, string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeListResult
            {
                Worktrees = entries,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeListResult));
    }
}
