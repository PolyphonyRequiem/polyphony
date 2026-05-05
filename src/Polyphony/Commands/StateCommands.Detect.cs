using System.Text.Json;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Root-workflow state detector. Replaces <c>scripts/detect-state.ps1</c>.
/// Inspects ADO state, plan artifacts on disk, and git state to produce
/// the canonical lifecycle snapshot the root YAML routes on.
/// </summary>
public sealed partial class StateCommands
{
    private static readonly Regex GitHubSlugRegex =
        new(@"github\.com[:/]([^/]+/[^/]+?)(?:\.git)?(?:[/?#].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YamlFrontmatterRegex =
        new(@"^---\s*\r?\n(.*?)\r?\n---", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WorkItemIdRegex =
        new(@"work_item_id:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex LegacyWorkItemRowRegex =
        new(@"\|\s*\*{0,2}Work\s*Item\*{0,2}\s*\|\s*#(\d+)", RegexOptions.Compiled);
    private static readonly Regex LegacyAnyLabelRowRegex =
        new(@"\|\s*\*{0,2}[^|*]+\*{0,2}\s*\|\s*#(\d+)", RegexOptions.Compiled);

    private const string DefaultPlanRoot = "docs/projects";

    /// <summary>
    /// Inspect work item lifecycle state, plan artifacts, and git/PR state
    /// to produce the canonical root-workflow routing payload.
    /// </summary>
    /// <param name="workItem">ADO work item ID to inspect.</param>
    /// <param name="intent">User intent: <c>new</c>, <c>redo</c>, or <c>resume</c>. Defaults to <c>resume</c>.</param>
    /// <param name="planPath">Explicit plan file override (debugging/recovery). Empty = filesystem fallback.</param>
    /// <param name="planRoot">Directory glob root for filesystem plan discovery (default <c>docs/projects</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("detect")]
    public async Task<int> Detect(
        int workItem,
        string intent = "resume",
        string planPath = "",
        string planRoot = DefaultPlanRoot,
        CancellationToken ct = default)
    {
        if (intent is not ("new" or "redo" or "resume"))
        {
            EmitDetectError(workItem, intent, $"Invalid intent '{intent}'. Must be one of: new, redo, resume.");
            return ExitCodes.ConfigError;
        }

        try
        {
            return await DetectInternalAsync(workItem, intent, planPath, planRoot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitDetectError(workItem, intent, ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    private async Task<int> DetectInternalAsync(
        int workItem, string intent, string planPath, string planRoot, CancellationToken ct)
    {
        // Resolve GH_TOKEN early so all downstream gh calls use the correct identity.
        await ghTokenResolver.ResolveAsync(ct).ConfigureAwait(false);

        var adoOrg = await SafeConfigReadAsync("organization", ct).ConfigureAwait(false) ?? "";
        var adoProject = await SafeConfigReadAsync("project", ct).ConfigureAwait(false) ?? "";
        var adoWorkspace = (string.IsNullOrEmpty(adoOrg) || string.IsNullOrEmpty(adoProject))
            ? "" : $"{adoOrg}/{adoProject}";

        try { await twig.SyncAsync(ct).ConfigureAwait(false); } catch { /* non-fatal */ }

        var (planStatus, planSource, resolvedPlanPath) = DiscoverPlan(workItem, planPath, planRoot);
        var hasPlan = planStatus == "complete";

        try { await twig.SetActiveAsync(workItem, ct).ConfigureAwait(false); } catch { /* non-fatal */ }

        var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        if (item is null)
        {
            EmitDetectError(workItem, intent, $"Work item {workItem} not found");
            return ExitCodes.CacheError;
        }

        var children = await repository.GetChildrenAsync(workItem, ct).ConfigureAwait(false);
        var routeDecision = phaseDetector.Detect(item, children);
        var workspaceHint = BranchNameResolver.Resolve(processConfig, item);

        var (phase, action) = routeDecision switch
        {
            NeedsPlanning            => (SdlcPhase.NeedsPlanning, SdlcAction.Plan),
            NeedsSeeding             => (SdlcPhase.NeedsSeeding, SdlcAction.Seed),
            ReadyForImplementation   => (SdlcPhase.ReadyForImplementation, SdlcAction.Implement),
            ImplementationInProgress => (SdlcPhase.InProgress, SdlcAction.Monitor),
            ReadyForCompletion       => (SdlcPhase.ReadyForCompletion, SdlcAction.Close),
            RoutingDone              => (SdlcPhase.Done, SdlcAction.None),
            RoutingRemoved           => (SdlcPhase.Removed, SdlcAction.None),
            _                        => (SdlcPhase.Unknown, SdlcAction.None),
        };

        var implementationStatus = action switch
        {
            SdlcAction.Plan => "not_started",
            SdlcAction.Seed => "not_started",
            SdlcAction.Implement => "not_started",
            SdlcAction.Monitor => "in_progress",
            SdlcAction.Close => "done",
            SdlcAction.None => phase switch
            {
                SdlcPhase.Done => "done",
                SdlcPhase.Removed => "removed",
                _ => "not_started",
            },
            _ => action,
        };

        var hierarchy = await hierarchyWalker.WalkAsync(workItem, maxDepth: 2, ct).ConfigureAwait(false);
        var workItemType = hierarchy?.Type ?? item.Type.Value ?? "";
        var workItemState = hierarchy?.State ?? item.State ?? "";
        var workItemTitle = hierarchy?.Title ?? item.Title ?? "";

        var directChildren = hierarchy?.Children ?? [];
        var childCount = directChildren.Length;
        var doneCount = directChildren.Count(c => c.State == "Done");
        var doingCount = directChildren.Count(c => c.State == "Doing");
        var todoCount = directChildren.Count(c => c.State == "To Do");

        var hasSeededChildren = childCount > 0;
        var anyChildMissingTasks = directChildren.Any(c =>
            c.State != "Done" && (c.Children is null || c.Children.Length == 0));

        var seedStatus = (childCount, anyChildMissingTasks) switch
        {
            (0, _) => "unseeded",
            (_, true) => "partial",
            _ => "seeded",
        };

        if (!string.IsNullOrEmpty(workspaceHint?.FeatureBranch))
        {
            implementationStatus = await CheckUnmergedBranchesAsync(
                workspaceHint.FeatureBranch, implementationStatus, ct).ConfigureAwait(false);
        }

        // Check if the feature branch exists on the remote (diagnostic telemetry).
        var featureBranchExists = false;
        if (!string.IsNullOrEmpty(workspaceHint?.FeatureBranch))
        {
            try
            {
                var refs = await git.LsRemoteHeadsAsync("origin", workspaceHint.FeatureBranch, ct)
                    .ConfigureAwait(false);
                featureBranchExists = refs.Count > 0;
            }
            catch { /* non-fatal — default to false */ }
        }

        var validation = transitionValidator.Validate(item, "begin_planning", children);
        if (validation is ValidTransition v && workItemState == "To Do" && hasSeededChildren)
        {
            try
            {
                await twig.SetActiveAsync(workItem, ct).ConfigureAwait(false);
                await twig.SetStateAsync(v.TargetState, ct).ConfigureAwait(false);
                workItemState = v.TargetState;
            }
            catch { /* non-fatal — surface through phase routing on next pass */ }
        }

        var (intentConflict, needsCleanup) = intent switch
        {
            "new" when (hasSeededChildren || hasPlan) => (true, false),
            "redo" => (false, hasSeededChildren || hasPlan),
            _ => (false, false),
        };

        var errorMsg = "";
        if (!intentConflict && !needsCleanup && planStatus == "ambiguous")
        {
            errorMsg = "Plan status is ambiguous: multiple plan sources detected. Resolve before proceeding.";
        }

        var childrenSummaryJson = JsonSerializer.Serialize(
            new ChildrenStateCounts(childCount, doneCount, doingCount, todoCount),
            PolyphonyJsonContext.Default.ChildrenStateCounts);

        var result = new StateDetectResult
        {
            WorkItemId = workItem,
            WorkItemType = workItemType,
            WorkItemState = workItemState,
            WorkItemTitle = workItemTitle,
            Intent = intent,
            Phase = phase,
            HasPlan = hasPlan,
            PlanStatus = planStatus,
            PlanPath = resolvedPlanPath,
            PlanSource = planSource,
            HasSeededChildren = hasSeededChildren,
            AnyChildMissingTasks = anyChildMissingTasks,
            SeedStatus = seedStatus,
            ChildrenSummary = childrenSummaryJson,
            ImplementationStatus = implementationStatus,
            WorkspaceHint = workspaceHint ?? new WorkspaceHint(),
            AdoOrg = adoOrg,
            AdoProject = adoProject,
            AdoWorkspace = adoWorkspace,
            IntentConflict = intentConflict,
            NeedsCleanup = needsCleanup,
            FeatureBranchExists = featureBranchExists,
            Error = errorMsg,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.StateDetectResult));
        return ExitCodes.Success;
    }

    private async Task<string?> SafeConfigReadAsync(string key, CancellationToken ct)
    {
        try { return await twig.GetConfigValueAsync(key, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task<string> CheckUnmergedBranchesAsync(
        string featureBranch, string currentImpl, CancellationToken ct)
    {
        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug)) return currentImpl;

        try
        {
            var heads = await git.LsRemoteHeadsAsync("origin", $"{featureBranch}*", ct).ConfigureAwait(false);
            if (heads.Count == 0) return currentImpl;

            // Timeout the gh call to prevent indefinite hangs (e.g. auth
            // issues, network, rate limits). Degrade gracefully on timeout.
            using var ghTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ghTimeout.CancelAfter(TimeSpan.FromSeconds(30));

            var openPrs = await gh.ListPullRequestsAsync(
                slug, new PrListFilters(Head: featureBranch, State: "open"), ghTimeout.Token).ConfigureAwait(false);
            if (openPrs.Count > 0 && currentImpl != "done") return "in_progress";
        }
        catch { /* non-fatal — timeout or network error degrades to current status */ }
        return currentImpl;
    }

    private async Task<string> TryResolveSlugAsync(CancellationToken ct)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return "";
            var match = GitHubSlugRegex.Match(url);
            return match.Success ? match.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Filesystem-fallback plan discovery. Mirrors the priority chain in
    /// detect-state.ps1: explicit override → frontmatter scan → legacy table.
    /// Public for testability.
    /// </summary>
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

    private void EmitDetectError(int workItemId, string intent, string message)
    {
        var result = new StateDetectResult
        {
            WorkItemId = workItemId,
            WorkItemType = "",
            WorkItemState = "",
            WorkItemTitle = "",
            Intent = intent,
            Phase = "error",
            HasPlan = false,
            PlanStatus = "none",
            PlanPath = "",
            PlanSource = "none",
            HasSeededChildren = false,
            AnyChildMissingTasks = false,
            SeedStatus = "unseeded",
            ChildrenSummary = "{\"total\":0,\"done\":0,\"doing\":0,\"todo\":0}",
            ImplementationStatus = "not_started",
            WorkspaceHint = new WorkspaceHint(),
            AdoOrg = "",
            AdoProject = "",
            AdoWorkspace = "",
            IntentConflict = false,
            NeedsCleanup = false,
            FeatureBranchExists = false,
            Error = message,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.StateDetectResult));
    }
}

/// <summary>Children state count summary; serialised as the
/// <c>children_summary</c> JSON string field of <see cref="StateDetectResult"/>.</summary>

