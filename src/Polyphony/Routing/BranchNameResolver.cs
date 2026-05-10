using Polyphony.Configuration;
using Twig.Domain.Aggregates;

namespace Polyphony.Routing;

/// <summary>
/// Resolves branch name templates from <see cref="ProcessConfig.BranchStrategy"/>
/// by substituting placeholders with work item metadata.
/// </summary>
public static class BranchNameResolver
{
    /// <summary>
    /// Resolves branch names for the given work item using the branch strategy templates.
    /// Substitutes <c>{id}</c> with the root item's ID and <c>{slug}</c> with a URL-safe
    /// slug derived from the item title.
    /// </summary>
    /// <param name="config">The process config containing branch strategy templates.</param>
    /// <param name="rootItem">The root work item whose metadata drives branch naming.</param>
    /// <returns>A <see cref="WorkspaceHint"/> with resolved branch names, or null if no branch strategy is configured.</returns>
    public static WorkspaceHint? Resolve(ProcessConfig config, WorkItem rootItem)
    {
        if (config.BranchStrategy is null)
            return null;

        var slug = Slugify(rootItem.Title);

        return new WorkspaceHint
        {
            FeatureBranch = SubstitutePlaceholders(config.BranchStrategy.FeatureBranch, rootItem.Id, slug),
            // BranchStrategy.MergeGroupBranch is the canonical YAML wire key on the
            // user's process-config.yaml (the legacy `pg_branch:` key is
            // copied into MergeGroupBranch by ProcessConfigLoader for back-compat).
            // The JSON output emits "pg_branch" via [JsonPropertyName] until
            // the workflow rewire PR removes the bridge.
            MergeGroupBranch = SubstitutePlaceholders(config.BranchStrategy.MergeGroupBranch, rootItem.Id, slug),
        };
    }

    private static string SubstitutePlaceholders(string template, int id, string slug)
    {
        if (string.IsNullOrEmpty(template))
            return "";

        return template
            .Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{root_id}", id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{slug}", slug, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts a title string to a URL-safe slug: lowercase, alphanumeric and hyphens only,
    /// truncated to 50 characters.
    /// </summary>
    public static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var chars = new char[title.Length];
        var len = 0;
        var lastWasHyphen = true; // prevents leading hyphen

        for (var i = 0; i < title.Length; i++)
        {
            var c = title[i];

            if (char.IsLetterOrDigit(c))
            {
                chars[len++] = char.ToLowerInvariant(c);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                chars[len++] = '-';
                lastWasHyphen = true;
            }
        }

        // Trim trailing hyphen
        if (len > 0 && chars[len - 1] == '-')
            len--;

        // Truncate to 50 chars
        if (len > 50)
            len = 50;

        // Trim trailing hyphen after truncation
        if (len > 0 && chars[len - 1] == '-')
            len--;

        return new string(chars, 0, len);
    }
}
