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

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["branch", "--show-current"], ct).ConfigureAwait(false);
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

    public async Task FetchAsync(string remote, string refspec, CancellationToken ct = default)
    {
        string[] args = ["fetch", remote, refspec];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
    }

    private static string? TrimOrNull(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
