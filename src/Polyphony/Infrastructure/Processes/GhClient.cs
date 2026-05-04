using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="IGhClient"/> backed by <see cref="IProcessRunner"/>.
/// </summary>
public sealed class GhClient(IProcessRunner runner) : IGhClient
{
    private const string Exe = "gh";

    public async Task<GhAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["auth", "status"], ct).ConfigureAwait(false);
        // gh writes its detail message to stderr regardless of state.
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        return new GhAuthStatus(result.Succeeded, detail.Trim());
    }

    public async Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        string repoSlug,
        PrListFilters filters,
        CancellationToken ct = default)
    {
        var args = new List<string> { "pr", "list", "--repo", repoSlug };
        if (filters.Head is not null)
        {
            args.AddRange(["--head", filters.Head]);
        }
        if (filters.Base is not null)
        {
            args.AddRange(["--base", filters.Base]);
        }
        if (filters.State is not null)
        {
            args.AddRange(["--state", filters.State]);
        }
        if (filters.Limit is int limit)
        {
            args.AddRange(["--limit", limit.ToString()]);
        }
        args.AddRange(["--json", "number,headRefName,url,mergedAt"]);

        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return ParsePrList(result.Stdout);
    }

    public async Task<string> CreatePullRequestAsync(
        string repoSlug,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "create",
            "--repo", repoSlug,
            "--base", baseBranch,
            "--head", headBranch,
            "--title", title,
            "--body", body,
        ];

        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
        }

        var url = result.Stdout.Trim();
        if (string.IsNullOrEmpty(url))
        {
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, "gh pr create returned no URL");
        }
        return url;
    }

    private static IReadOnlyList<PullRequestSummary> ParsePrList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            return [];
        }

        var summaries = new List<PullRequestSummary>(array.Count);
        foreach (var item in array)
        {
            if (item is null) continue;
            var number = item["number"]?.GetValue<int>() ?? 0;
            var head = item["headRefName"]?.GetValue<string>() ?? string.Empty;
            var url = item["url"]?.GetValue<string>();
            var mergedAtRaw = item["mergedAt"]?.GetValue<string>();
            DateTimeOffset? mergedAt = null;
            if (!string.IsNullOrEmpty(mergedAtRaw)
                && DateTimeOffset.TryParse(mergedAtRaw, out var parsed))
            {
                mergedAt = parsed;
            }
            summaries.Add(new PullRequestSummary(number, head, url, mergedAt));
        }
        return summaries;
    }
}
