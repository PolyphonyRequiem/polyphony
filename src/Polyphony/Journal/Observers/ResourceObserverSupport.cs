using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Journal.Observers;

internal static class ResourceObserverSupport
{
    private static readonly Regex WorkItemRegex = new(@"^workitem:(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PullNumberRegex = new(@"^pr#(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitHubPullUrlRegex = new(@"^https://github\.com/([^/]+/[^/]+)/pull/(\d+)(?:[/?#].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AdoPullUrlRegex = new(@"^https://dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)/pullrequest/(\d+)(?:[/?#].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? GetStringAttribute(JsonObject? attributes, string name)
    {
        if (attributes is null || !attributes.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        return node.ToString();
    }

    public static int? GetIntAttribute(JsonObject? attributes, string name)
    {
        if (attributes is null || !attributes.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<string>(out var stringValue)
                && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    public static bool TryParseWorkItemId(string id, out int workItemId)
    {
        workItemId = 0;
        var match = WorkItemRegex.Match(id);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out workItemId);
    }

    public static bool TryParseWorkItemTagId(string id, out int workItemId, out string tag)
    {
        workItemId = 0;
        tag = string.Empty;

        var separator = id.IndexOf(':');
        if (separator <= 0 || separator >= id.Length - 1)
        {
            return false;
        }

        return int.TryParse(id[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out workItemId)
            && (tag = id[(separator + 1)..]).Length > 0;
    }

    public static bool TryParsePullRequestNumber(string id, out int pullRequestNumber)
    {
        pullRequestNumber = 0;
        var match = PullNumberRegex.Match(id);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pullRequestNumber);
    }

    public static bool TryParseGitHubPullRequest(string id, out string slug, out int pullRequestNumber)
    {
        slug = string.Empty;
        pullRequestNumber = 0;

        var match = GitHubPullUrlRegex.Match(id);
        return match.Success
            && (slug = match.Groups[1].Value).Length > 0
            && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pullRequestNumber);
    }

    public static bool TryParseAdoPullRequest(string id, out RepoIdentity.AdoRepo repo, out int pullRequestNumber)
    {
        repo = new RepoIdentity.AdoRepo(string.Empty, string.Empty, string.Empty);
        pullRequestNumber = 0;

        var match = AdoPullUrlRegex.Match(id);
        return match.Success
            && (repo = new RepoIdentity.AdoRepo(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value)) is not null
            && int.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pullRequestNumber);
    }

    public static string StripRefsHeadsPrefix(string value)
    {
        const string prefix = "refs/heads/";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : value;
    }

    public static string? GetBranchExpectedSha(JsonObject? attributes)
        => GetStringAttribute(attributes, "new_sha")
            ?? GetStringAttribute(attributes, "sha")
            ?? GetStringAttribute(attributes, "commit_sha");

    public static bool MatchesPolyphonyBranchPattern(int rootId, string branchName)
    {
        var prefix = rootId.ToString(CultureInfo.InvariantCulture);
        return string.Equals(branchName, $"feature/{prefix}", StringComparison.Ordinal)
            || string.Equals(branchName, $"plan/{prefix}", StringComparison.Ordinal)
            || branchName.StartsWith($"plan/{prefix}-", StringComparison.Ordinal)
            || branchName.StartsWith($"mg/{prefix}_", StringComparison.Ordinal)
            || branchName.StartsWith($"impl/{prefix}-", StringComparison.Ordinal)
            || string.Equals(branchName, $"evidence/{prefix}", StringComparison.Ordinal)
            || branchName.StartsWith($"evidence/{prefix}-", StringComparison.Ordinal);
    }

    public static JsonObject CreateActualAttributes(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            result[name] = JsonValue.Create(value);
        }

        return result;
    }
}
