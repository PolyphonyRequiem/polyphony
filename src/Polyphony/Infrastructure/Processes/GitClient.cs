namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="IGitClient"/> backed by <see cref="IProcessRunner"/>.
/// </summary>
public sealed class GitClient(IProcessRunner runner) : IGitClient
{
    private const string Exe = "git";

    public async Task<string?> GetTopLevelAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<string?> GetCommonDirAsync(CancellationToken ct = default)
    {
        // --path-format=absolute MUST come before --git-common-dir on the
        // command line — git's rev-parse arg parser is left-to-right and
        // requires the format flag earlier in the line to apply to the
        // path-emitting flag that follows. Per the Rev 4.2 amendment, the
        // absolute form is non-negotiable: a relative path resolves
        // differently from each worktree's cwd and breaks the cross-worktree
        // convergence the common dir is meant to provide.
        var result = await runner.RunAsync(
            Exe,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<bool> IsBareRepositoryAsync(string commonDir, CancellationToken ct = default)
    {
        // Address the gitdir explicitly via --git-dir, not via cwd
        // discovery — see the IGitClient doc-comment for why both
        // worktree resolution and safe.bareRepository=explicit demand
        // this form.
        string[] args = ["--git-dir", commonDir, "rev-parse", "--is-bare-repository"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
        }

        var trimmed = result.Stdout.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["branch", "--show-current"], ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        var result = await runner.RunAsync(
            Exe,
            ["-C", workingDirectory, "branch", "--show-current"],
            ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["remote", "get-url", remote], ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["branch", "-r"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var branches = new List<string>();
        foreach (var raw in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // git branch -r emits lines like "  origin/main" or "  origin/HEAD -> origin/main".
            // We only want the simple branch names with the origin/ prefix stripped.
            if (raw.Contains("->", StringComparison.Ordinal))
            {
                continue;
            }
            var trimmed = raw.StartsWith("origin/", StringComparison.Ordinal)
                ? raw["origin/".Length..]
                : raw;
            branches.Add(trimmed);
        }
        return branches;
    }

    public async Task<IReadOnlyList<string>> LsRemoteHeadsAsync(string remote, string pattern, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["ls-remote", "--heads", remote, pattern], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }
        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public async Task<string?> RevParseLocalBranchAsync(string branch, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["rev-parse", "--verify", $"refs/heads/{branch}"], ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task CheckoutAsync(string branch, CancellationToken ct = default)
    {
        string[] args = ["checkout", branch];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task CreateBranchAsync(string branch, string? startPoint = null, CancellationToken ct = default)
    {
        string[] args = startPoint is not null
            ? ["checkout", "-b", branch, startPoint]
            : ["checkout", "-b", branch];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task CheckoutTrackingAsync(string branch, string remote = "origin", CancellationToken ct = default)
    {
        string[] args = ["checkout", "--track", $"{remote}/{branch}"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task PushAsync(string branch, string remote = "origin", CancellationToken ct = default)
    {
        string[] args = ["push", "-u", remote, branch];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task<ProcessResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remote);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        string[] args = ["push", remote, "--delete", branch];
        return await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
    }

    public async Task FetchAsync(string remote, string refspec, CancellationToken ct = default)
    {
        string[] args = ["fetch", remote, refspec];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task<IReadOnlyList<string>> GetStatusAsync(CancellationToken ct = default)
    {
        string[] args = ["status", "--porcelain"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            // status --porcelain prefixes each entry with "XY " (the index/worktree status pair).
            // Returning the raw lines is more useful for diagnostics than stripping.
            .Select(line => line.TrimEnd('\r'))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetStatusAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        // --no-optional-locks: we never want this read probe to touch the
        // index lock. Two concurrent gate callers (e.g. launcher + driver
        // racing) must coexist without serialising on the lock or rewriting
        // the index timestamp behind the operator's back.
        string[] args = ["-C", workingDirectory, "--no-optional-locks", "status", "--porcelain"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();
    }

    public async Task<string?> GetInProgressOperationAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        // Resolve the per-worktree gitdir (NOT --git-common-dir, which
        // points at the bare/main .git for linked worktrees — the
        // operation sentinels live in the per-worktree dir).
        var revParse = await runner.RunAsync(
            Exe,
            ["-C", workingDirectory, "rev-parse", "--path-format=absolute", "--git-dir"],
            ct).ConfigureAwait(false);
        if (!revParse.Succeeded) return null;

        var gitDir = TrimOrNull(revParse.Stdout);
        if (string.IsNullOrEmpty(gitDir)) return null;

        // Order of checks: rebase variants first because rebase-merge
        // can also leave a CHERRY_PICK_HEAD-shaped sentinel (am-style)
        // and we want the more-specific operation name.
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))) return "rebase-merge";
        if (Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "rebase-apply";
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return "bisect";
        return null;
    }

    public async Task StageAsync(string pathspec, CancellationToken ct = default)
    {
        string[] args = ["add", "--", pathspec];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task CommitAsync(string message, CancellationToken ct = default)
    {
        string[] args = ["commit", "-m", message];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task ResetHardAsync(string refspec, CancellationToken ct = default)
    {
        string[] args = ["reset", "--hard", refspec];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task<string?> ShowFileAtRefAsync(string refspec, string path, CancellationToken ct = default)
    {
        string[] args = ["show", $"{refspec}:{path}"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (result.Succeeded) return result.Stdout;
        // git exits non-zero when the ref or path doesn't exist; distinguish
        // missing-path from real failures by sniffing stderr.
        var stderr = result.Stderr ?? "";
        if (stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase)
            || (stderr.Contains("path '", StringComparison.OrdinalIgnoreCase) && stderr.Contains("does not exist in", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, stderr);
    }

    public Task<ProcessResult> WorktreeAddAsync(string branch, string path, string? gitRef, CancellationToken ct = default)
    {
        // git worktree add -b {branch} {path} [{ref}]
        // Reference defaults to HEAD when omitted — pass it through verbatim
        // when supplied so callers can pin to any revspec git understands.
        string[] args = string.IsNullOrEmpty(gitRef)
            ? ["worktree", "add", "-b", branch, path]
            : ["worktree", "add", "-b", branch, path, gitRef];
        return runner.RunAsync(Exe, args, ct);
    }

    public Task<ProcessResult> WorktreeRemoveAsync(string path, bool force, CancellationToken ct = default)
    {
        string[] args = force
            ? ["worktree", "remove", "--force", path]
            : ["worktree", "remove", path];
        return runner.RunAsync(Exe, args, ct);
    }

    public Task<ProcessResult> WorktreeListAsync(CancellationToken ct = default)
        => runner.RunAsync(Exe, ["worktree", "list", "--porcelain"], ct);

    public async Task<RebaseOutcome> RebaseOntoAsync(
        string newBase,
        string oldBase,
        string head,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(newBase);
        ArgumentException.ThrowIfNullOrEmpty(oldBase);
        ArgumentException.ThrowIfNullOrEmpty(head);

        // 1. Detach onto the head we plan to replay.
        string[] checkoutArgs = ["checkout", "--detach", head];
        var checkout = await runner.RunAsync(Exe, checkoutArgs, ct).ConfigureAwait(false);
        if (!checkout.Succeeded)
        {
            return new RebaseOutcome.Failed(
                string.IsNullOrEmpty(checkout.Stderr) ? checkout.Stdout : checkout.Stderr);
        }

        // 2. Replay only [oldBase..head] onto newBase.
        string[] rebaseArgs = ["rebase", "--onto", newBase, oldBase, "HEAD"];
        var rebase = await runner.RunAsync(Exe, rebaseArgs, ct).ConfigureAwait(false);

        if (rebase.Succeeded)
        {
            // 3a. Capture the new HEAD sha.
            var head2 = await runner.RunAsync(Exe, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
            if (!head2.Succeeded)
            {
                // Should be rare — rebase succeeded but rev-parse failed. Treat as Failed
                // for caller routing rather than guessing a SHA from rebase stdout.
                return new RebaseOutcome.Failed(
                    string.IsNullOrEmpty(head2.Stderr) ? head2.Stdout : head2.Stderr);
            }
            return new RebaseOutcome.Clean(head2.Stdout.Trim());
        }

        // 3b. Conflict OR hard failure. Sniff before aborting because git
        // reports CONFLICT lines on stdout and we want to preserve them.
        var conflicts = ParseConflictedFiles(rebase.Stdout, rebase.Stderr);

        // Always abort defensively so the worktree returns to detached-HEAD
        // state at `head` with no .git/rebase-* directory left behind.
        await TryRebaseAbortAsync(ct).ConfigureAwait(false);

        if (conflicts.Count > 0)
        {
            return new RebaseOutcome.Conflict(conflicts);
        }

        return new RebaseOutcome.Failed(
            string.IsNullOrEmpty(rebase.Stderr) ? rebase.Stdout : rebase.Stderr);
    }

    public async Task<string?> MergeBaseAsync(string a, string b, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(a);
        ArgumentException.ThrowIfNullOrEmpty(b);

        var result = await runner.RunAsync(Exe, ["merge-base", a, b], ct).ConfigureAwait(false);
        return result.Succeeded ? TrimOrNull(result.Stdout) : null;
    }

    public async Task<bool> IsAncestorAsync(string maybeAncestor, string descendant, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(maybeAncestor);
        ArgumentException.ThrowIfNullOrEmpty(descendant);

        string[] args = ["merge-base", "--is-ancestor", maybeAncestor, descendant];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        return result.ExitCode switch
        {
            // git's documented contract: exit 0 = ancestor, exit 1 = not.
            0 => true,
            1 => false,
            _ => throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr),
        };
    }

    public Task<ProcessResult> PushHeadWithLeaseAsync(
        string remote,
        string branch,
        string expectedRemoteSha,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remote);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(expectedRemoteSha);

        // Full ref form (refs/heads/{branch}) on both sides of the lease so the
        // server-side comparison is unambiguous — see `git push --help`,
        // "force-with-lease=<refname>:<expect>" section.
        string[] args =
        [
            "push", remote,
            $"HEAD:refs/heads/{branch}",
            $"--force-with-lease=refs/heads/{branch}:{expectedRemoteSha}",
        ];
        return runner.RunAsync(Exe, args, ct);
    }

    private async Task TryRebaseAbortAsync(CancellationToken ct)
    {
        // Best-effort. If there's no rebase in progress git exits non-zero;
        // we don't care because the worktree is already where we want it.
        try
        {
            await runner.RunAsync(Exe, ["rebase", "--abort"], ct).ConfigureAwait(false);
        }
        catch
        {
            // Swallow — rebase --abort is purely a cleanup gesture.
        }
    }

    private static IReadOnlyList<string> ParseConflictedFiles(string stdout, string stderr)
    {
        // git rebase emits lines like:
        //   CONFLICT (content): Merge conflict in path/to/file
        //   CONFLICT (rename/delete): foo.txt deleted in HEAD and renamed to bar.txt in <commit>
        // The path is everything after the last " in " on a "CONFLICT (content):" line, which
        // is brittle for the general case but reliable for `Merge conflict in <path>`.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var raw in (stdout + "\n" + stderr).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.Contains("CONFLICT", StringComparison.Ordinal)) continue;

            // "Merge conflict in <path>" — preferred shape
            var marker = "Merge conflict in ";
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var path = line[(idx + marker.Length)..].Trim();
                if (path.Length > 0 && seen.Add(path))
                {
                    files.Add(path);
                }
                continue;
            }

            // Fallback: take whatever is between the last "): " and end-of-line
            // for non-content conflicts (rename/delete, modify/delete, etc.).
            // This is best-effort — the exact format varies by conflict kind.
            var colon = line.IndexOf("): ", StringComparison.Ordinal);
            if (colon >= 0)
            {
                var rest = line[(colon + 3)..].Trim();
                if (rest.Length > 0 && seen.Add(rest))
                {
                    files.Add(rest);
                }
            }
        }
        return files;
    }

    private static string? TrimOrNull(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
