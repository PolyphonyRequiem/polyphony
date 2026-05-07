using System.Text.RegularExpressions;

namespace Polyphony.Commands;

/// <summary>
/// Filesystem plan-document discovery used by <c>state next-ready</c>.
/// Mirrors the priority chain of the legacy <c>detect-state.ps1</c> script:
/// explicit override → frontmatter scan → legacy table.
/// </summary>
public sealed partial class StateCommands
{
    private static readonly Regex YamlFrontmatterRegex =
        new(@"^---\s*\r?\n(.*?)\r?\n---", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WorkItemIdRegex =
        new(@"work_item_id:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex LegacyWorkItemRowRegex =
        new(@"\|\s*\*{0,2}Work\s*Item\*{0,2}\s*\|\s*#(\d+)", RegexOptions.Compiled);
    private static readonly Regex LegacyAnyLabelRowRegex =
        new(@"\|\s*\*{0,2}[^|*]+\*{0,2}\s*\|\s*#(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Resolve a work item's plan document via filesystem fallback.
    /// </summary>
    /// <returns>
    /// Tuple of (status, source, path) where status is
    /// <c>none</c> | <c>complete</c> | <c>ambiguous</c>.
    /// </returns>
    public static (string Status, string Source, string Path) DiscoverPlan(
        int workItemId, string explicitPath, string planRoot)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath)
                ? ("complete", "explicit_override", Path.GetFullPath(explicitPath))
                : ("none", "none", "");
        }

        if (!Directory.Exists(planRoot)) return ("none", "none", "");

        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(planRoot, "*.plan.md"))
        {
            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            if (PlanMatchesWorkItem(content, workItemId)) matches.Add(file);
        }

        return matches.Count switch
        {
            1 => ("complete", "filesystem_fallback", matches[0]),
            > 1 => ("ambiguous", "none", ""),
            _ => ("none", "none", ""),
        };
    }

    private static bool PlanMatchesWorkItem(string content, int workItemId)
    {
        var fm = YamlFrontmatterRegex.Match(content);
        if (fm.Success)
        {
            var idMatch = WorkItemIdRegex.Match(fm.Groups[1].Value);
            if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var id) && id == workItemId)
                return true;
        }

        var rowMatch = LegacyWorkItemRowRegex.Match(content);
        if (rowMatch.Success && int.TryParse(rowMatch.Groups[1].Value, out var rid) && rid == workItemId)
            return true;

        var anyRowMatch = LegacyAnyLabelRowRegex.Match(content);
        if (anyRowMatch.Success && int.TryParse(anyRowMatch.Groups[1].Value, out var arid) && arid == workItemId)
            return true;

        return false;
    }
}
