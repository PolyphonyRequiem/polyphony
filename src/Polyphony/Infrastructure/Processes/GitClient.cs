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

    private static string? TrimOrNull(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
