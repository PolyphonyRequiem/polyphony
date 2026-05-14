using System.Globalization;
using Polyphony.Branching;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Infrastructure.Worktrees;
using Polyphony.Routing;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;
using ParsedBranch = Polyphony.Branching.ParsedBranch;

namespace Polyphony.Reset;

/// <summary>
/// Enumerates every artifact a <c>polyphony reset</c> would touch for a
/// given root, <b>without performing any mutation</b>. Returns a
/// <see cref="ResetPlan"/> that can be serialized as the dry-run JSON
/// contract.
///
/// <para>Artifact categories:</para>
/// <list type="bullet">
///   <item>ADO tags matching <see cref="PolyphonyTag.IsPolyphonyOwned"/>
///         on the root and all in-scope descendants.</item>
///   <item>Polyphony-authored PR comment threads (closed, top-level
///         advisory threads on PRs whose source branch belongs to the
///         root).</item>
///   <item>Per-root state directory at
///         <c>&lt;git-common-dir&gt;/polyphony/N/</c>.</item>
///   <item>Local and remote branches matching the polyphony branch grammar
///         (<c>feature/N</c>, <c>plan/N*</c>, <c>impl/N-*</c>,
///         <c>mg/N_*</c>, <c>evidence/N-*</c>).</item>
///   <item>Worktrees under <c>polyphony-runs/apex-N/</c>.</item>
/// </list>
/// </summary>
public sealed class ResetPlanner(
    IWorkItemRepository repository,
    IGitClient git,
    HierarchyWalker walker,
    PolyphonyStatePaths statePaths,
    IAdoClient ado)
{
    /// <summary>
    /// Enumerate all artifacts that a reset of <paramref name="rootId"/>
    /// would touch. Performs zero mutations.
    /// </summary>
    /// <param name="rootId">ADO work item ID of the root to plan reset for.</param>
    /// <param name="organization">ADO organization (optional — enables comment enumeration).</param>
    /// <param name="project">ADO project (optional — enables comment enumeration).</param>
    /// <param name="adoRepository">ADO repository identifier (optional — enables comment enumeration).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ResetPlan> PlanAsync(
        int rootId,
        string? organization = null,
        string? project = null,
        string? adoRepository = null,
        CancellationToken ct = default)
    {
        var tagRemovals = await EnumerateTagRemovalsAsync(rootId, ct).ConfigureAwait(false);
        var (stateDir, stateDirExists) = await EnumerateStateDirAsync(rootId, ct).ConfigureAwait(false);
        var (localBranches, remoteBranches) = await EnumerateBranchesAsync(rootId, ct).ConfigureAwait(false);
        var worktrees = await EnumerateWorktreesAsync(rootId, ct).ConfigureAwait(false);

        var comments = await EnumerateCommentsAsync(
            rootId, localBranches, remoteBranches,
            organization, project, adoRepository, ct).ConfigureAwait(false);

        return new ResetPlan
        {
            RootId = rootId,
            TagRemovals = [.. tagRemovals],
            MatchingTags = tagRemovals.SelectMany(r => r.Tags).Distinct().ToArray(),
            AffectedItemIds = tagRemovals.Select(r => r.ItemId).ToArray(),
            Comments = comments.Length > 0 ? comments : null,
            StateDir = stateDir,
            StateDirExists = stateDirExists,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            Worktrees = worktrees,
        };
    }

    // ─── Tag enumeration ────────────────────────────────────────────────

    private async Task<IReadOnlyList<ResetPlanTagRemoval>> EnumerateTagRemovalsAsync(
        int rootId, CancellationToken ct)
    {
        var removals = new List<ResetPlanTagRemoval>();

        var hierarchy = await walker.WalkAsync(rootId, maxDepth: 10, ct).ConfigureAwait(false);
        if (hierarchy is null)
        {
            var rootItem = await repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
            if (rootItem is not null)
            {
                rootItem.Fields.TryGetValue("System.Tags", out var raw);
                var tags = CollectOwnedTags(raw);
                if (tags.Length > 0)
                    removals.Add(new ResetPlanTagRemoval { ItemId = rootId, Tags = tags });
            }
            return removals;
        }

        foreach (var node in Flatten(hierarchy))
        {
            var ownedTags = CollectOwnedTags(node.Tags);
            if (ownedTags.Length > 0)
                removals.Add(new ResetPlanTagRemoval { ItemId = node.WorkItemId, Tags = ownedTags });
        }

        return removals;
    }

    private static string[] CollectOwnedTags(string? rawTags)
    {
        var tagSet = TagSet.Parse(rawTags);
        return tagSet.Where(PolyphonyTag.IsPolyphonyOwned).ToArray();
    }

    private static IEnumerable<HierarchyResult> Flatten(HierarchyResult node)
    {
        yield return node;
        if (node.Children is null) yield break;
        foreach (var child in node.Children)
        {
            foreach (var sub in Flatten(child))
                yield return sub;
        }
    }

    // ─── State dir enumeration ──────────────────────────────────────────

    private async Task<(string? StateDir, bool Exists)> EnumerateStateDirAsync(
        int rootId, CancellationToken ct)
    {
        try
        {
            var stateDir = await statePaths.GetStateRootAsync(rootId, ct).ConfigureAwait(false);
            return (stateDir, Directory.Exists(stateDir));
        }
        catch (InvalidOperationException)
        {
            // Not in a git repo — state dir resolution is best-effort.
            return (null, false);
        }
    }

    // ─── Branch enumeration ─────────────────────────────────────────────

    private async Task<(string[] Local, string[] Remote)> EnumerateBranchesAsync(
        int rootId, CancellationToken ct)
    {
        var localBranches = await git.ListLocalBranchesAsync(ct).ConfigureAwait(false);
        var remoteBranches = await git.ListRemoteBranchesAsync(ct).ConfigureAwait(false);

        return (
            FilterBranchesByRoot(localBranches, rootId),
            FilterBranchesByRoot(remoteBranches, rootId));
    }

    internal static string[] FilterBranchesByRoot(IReadOnlyList<string> branches, int rootId)
    {
        var matching = new List<string>();
        var rid = RootId.Parse(rootId);
        var sdlcPrefix = $"sdlc/apex/{rootId.ToString(CultureInfo.InvariantCulture)}";

        foreach (var branch in branches)
        {
            var parsed = BranchNameParser.ParseOrUnrecognized(branch);
            var belongs = parsed switch
            {
                ParsedBranch.Feature f => f.RootId == rid,
                ParsedBranch.RootPlan p => p.RootId == rid,
                ParsedBranch.DescendantPlan p => p.RootId == rid,
                ParsedBranch.MergeGroup mg => mg.RootId == rid,
                ParsedBranch.Impl i => i.RootId == rid,
                ParsedBranch.Evidence e => e.RootId == rid,
                ParsedBranch.EvidenceOrphan => false,
                ParsedBranch.Unrecognized => false,
                _ => false
            };

            // Also match sdlc/apex/{rootId} which is outside the ParsedBranch grammar.
            if (!belongs && string.Equals(branch, sdlcPrefix, StringComparison.Ordinal))
                belongs = true;

            if (belongs)
                matching.Add(branch);
        }

        return [.. matching];
    }

    /// <summary>
    /// Returns true when a branch name (short form, no <c>refs/heads/</c>
    /// prefix) belongs to the given root under the polyphony branch grammar.
    /// Used to match PR source branches to a root without requiring the
    /// branch to exist locally.
    /// </summary>
    internal static bool BranchBelongsToRoot(string branchName, int rootId)
    {
        var rid = RootId.Parse(rootId);
        var parsed = BranchNameParser.ParseOrUnrecognized(branchName);
        var belongs = parsed switch
        {
            ParsedBranch.Feature f => f.RootId == rid,
            ParsedBranch.RootPlan p => p.RootId == rid,
            ParsedBranch.DescendantPlan p => p.RootId == rid,
            ParsedBranch.MergeGroup mg => mg.RootId == rid,
            ParsedBranch.Impl i => i.RootId == rid,
            ParsedBranch.Evidence e => e.RootId == rid,
            ParsedBranch.EvidenceOrphan => false,
            ParsedBranch.Unrecognized => false,
            _ => false
        };

        if (!belongs)
        {
            var sdlcPrefix = $"sdlc/apex/{rootId.ToString(CultureInfo.InvariantCulture)}";
            belongs = string.Equals(branchName, sdlcPrefix, StringComparison.Ordinal);
        }

        return belongs;
    }

    // ─── Worktree enumeration ───────────────────────────────────────────

    private async Task<string[]> EnumerateWorktreesAsync(int rootId, CancellationToken ct)
    {
        try
        {
            var commonDir = await git.GetCommonDirAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(commonDir))
                return [];

            var (runsRoot, _) = RunsRootResolver.Resolve(commonDir);
            var apexPrefix = Path.Combine(runsRoot, $"apex-{rootId.ToString(CultureInfo.InvariantCulture)}");
            // Normalize for consistent comparison.
            apexPrefix = Path.GetFullPath(apexPrefix);

            var listResult = await git.WorktreeListAsync(ct).ConfigureAwait(false);
            if (!listResult.Succeeded)
                return [];

            var entries = WorktreeCommands.ParsePorcelain(listResult.Stdout);
            var matching = new List<string>();

            foreach (var entry in entries)
            {
                var normalized = Path.GetFullPath(entry.Path);
                if (normalized.StartsWith(apexPrefix, StringComparison.OrdinalIgnoreCase))
                    matching.Add(entry.Path);
            }

            return [.. matching];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Worktree enumeration is best-effort.
            return [];
        }
    }

    // ─── Comment enumeration ────────────────────────────────────────────

    private async Task<ResetPlanComment[]> EnumerateCommentsAsync(
        int rootId,
        string[] localBranches,
        string[] remoteBranches,
        string? organization,
        string? project,
        string? adoRepository,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(organization)
            || string.IsNullOrEmpty(project)
            || string.IsNullOrEmpty(adoRepository))
            return [];

        try
        {
            // List all PRs (any status) and filter by source branch belonging to this root.
            var prs = await ado.ListPullRequestsAsync(
                organization, project, adoRepository,
                AdoPullRequestStatus.All, ct).ConfigureAwait(false);

            if (prs is null || prs.Count == 0)
                return [];

            var matchingPrs = prs.Where(pr =>
            {
                var sourceBranch = StripRefsHeads(pr.SourceRefName);
                return BranchBelongsToRoot(sourceBranch, rootId);
            }).ToList();

            if (matchingPrs.Count == 0)
                return [];

            var comments = new List<ResetPlanComment>();

            foreach (var pr in matchingPrs)
            {
                var threads = await ado.ListPullRequestThreadsAsync(
                    organization, project, adoRepository,
                    pr.PullRequestId, ct).ConfigureAwait(false);

                if (threads is null)
                    continue;

                // Polyphony posts advisory comments as closed, top-level threads
                // (no file path). This matches the pattern in PostCommentAdo which
                // creates threads with status 4 (closed) and commentType 1 (text).
                foreach (var thread in threads)
                {
                    if (string.Equals(thread.Status, "closed", StringComparison.OrdinalIgnoreCase)
                        && thread.FilePath is null)
                    {
                        comments.Add(new ResetPlanComment
                        {
                            PullRequestId = pr.PullRequestId,
                            ThreadId = thread.Id,
                        });
                    }
                }
            }

            return [.. comments];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // ADO comment enumeration is best-effort — PAT/network/config
            // failures should not block reset planning for local artifacts.
            return [];
        }
    }

    private static string StripRefsHeads(string refName)
    {
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.Ordinal)
            ? refName[prefix.Length..]
            : refName;
    }
}
